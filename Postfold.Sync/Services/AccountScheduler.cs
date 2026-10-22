using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Sync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Postfold.Sync.Services;

public sealed class AccountScheduler : IAccountScheduler
{
    private readonly object _gate = new object();
    private readonly Dictionary<AccountId, List<ScheduledWork>> _queues = new Dictionary<AccountId, List<ScheduledWork>>();
    private readonly Queue<AccountId> _turns = new Queue<AccountId>();
    private readonly HashSet<AccountId> _active = new HashSet<AccountId>();
    private readonly HashSet<AccountId> _activeReads = new HashSet<AccountId>();
    private readonly List<Task> _running = new List<Task>();
    private readonly Dictionary<AccountId, Task> _accountTasks = new Dictionary<AccountId, Task>();
    private readonly Dictionary<AccountId, CancellationTokenSource> _accountStops = new Dictionary<AccountId, CancellationTokenSource>();
    private readonly HashSet<AccountId> _suspended = new HashSet<AccountId>();
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        }
    );
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private readonly TimeProvider _time;
    private readonly IWorkPolicy _policy;
    private readonly IRetryDelayPolicy _retry;
    private readonly int _workers;
    private readonly int _queueLimit;
    private readonly int _attemptLimit;
    private readonly Task _pump;
    private int _count;
    private int _interactiveTurns;
    private bool _stopping;
    private bool _paused;

    public AccountScheduler(
        int workers,
        int queueLimit,
        int attemptLimit,
        TimeProvider time,
        IWorkPolicy policy,
        IRetryDelayPolicy retry
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptLimit);
        _workers = workers;
        _queueLimit = queueLimit;
        _attemptLimit = attemptLimit;
        _time = time;
        _policy = policy;
        _retry = retry;
        _pump = Task.Run(PumpAsync);
    }

    public Task<Failure> EnqueueAsync(
        AccountId account,
        IAccountWork work,
        WorkPriority priority,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<Failure> completion = new TaskCompletionSource<Failure>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_paused)
            {
                return Task.FromResult(Failure.PolicyBlocked);
            }
            if (_suspended.Contains(account))
            {
                return Task.FromResult(Failure.Unavailable);
            }
            if (!_accountStops.ContainsKey(account))
            {
                _accountStops.Add(account, new CancellationTokenSource());
            }
            if (_count >= _queueLimit)
            {
                return Task.FromResult(Failure.Unavailable);
            }

            if (!_queues.TryGetValue(account, out List<ScheduledWork>? queue))
            {
                queue = new List<ScheduledWork>();
                _queues.Add(account, queue);
                _turns.Enqueue(account);
            }

            queue.Add(new ScheduledWork(account, work, priority, cancellationToken, completion));
            _count++;
        }

        _signal.Writer.TryWrite(0);
        return completion.Task;
    }

    /// <summary>Stops new work, cancels queued/running work and observes this account before disconnecting it.</summary>
    public async Task SuspendAsync(AccountId account)
    {
        Task running;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            _suspended.Add(account);
            _accountStops.TryGetValue(account, out stop);
            if (_queues.TryGetValue(account, out List<ScheduledWork>? queue))
            {
                foreach (ScheduledWork work in queue)
                {
                    work.Completion.TrySetResult(Failure.Unavailable);
                }
                _count -= queue.Count;
                queue.Clear();
            }
            running = _accountTasks.GetValueOrDefault(account) ?? Task.CompletedTask;
        }
        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
        await running.ConfigureAwait(false);
    }

    public void Resume(AccountId account)
    {
        lock (_gate)
        {
            if (_suspended.Remove(account) && _accountStops.Remove(account, out CancellationTokenSource? previous))
            {
                previous.Dispose();
            }
        }
    }

    /// <summary>Stops admissions and observes active work without cancelling a submission. Cancellation leaves admission paused until ResumeAll.</summary>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task[] running;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _paused = true;
            foreach (ScheduledWork work in _queues.Values.SelectMany(queue => queue))
            {
                work.Completion.TrySetResult(Failure.PolicyBlocked);
            }
            _queues.Clear();
            _turns.Clear();
            _count = 0;
            running = _running.ToArray();
        }
        await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumes admissions against current durable state; discarded scheduled callbacks are never restored.</summary>
    public void ResumeAll()
    {
        lock (_gate)
        {
            _paused = false;
        }
        _signal.Writer.TryWrite(0);
    }

    public async ValueTask DisposeAsync()
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_stopping;
            _stopping = true;
        }

        if (cancel)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _signal.Writer.TryComplete();
        }

        await _pump.ConfigureAwait(false);
        foreach (CancellationTokenSource stop in _accountStops.Values)
        {
            stop.Dispose();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (byte signal in _signal.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                List<ScheduledWork> ready = new List<ScheduledWork>();
                lock (_gate)
                {
                    if (_paused)
                    {
                        continue;
                    }
                    _running.RemoveAll(task => task.IsCompleted);
                    int turns = _turns.Count;
                    while (turns-- > 0 && _turns.TryDequeue(out AccountId account))
                    {
                        if (!_queues.TryGetValue(account, out List<ScheduledWork>? queue))
                        {
                            continue;
                        }

                        if (queue.Count == 0 && !_active.Contains(account) && !_activeReads.Contains(account))
                        {
                            _queues.Remove(account);
                            continue;
                        }

                        // One bounded read slot remains available even when all serial account workers
                        // are syncing. Only explicitly independent, interactive reads can use it.
                        ScheduledWork? read = _activeReads.Count == 0 ? queue.FirstOrDefault(item =>
                            item.Priority == WorkPriority.Interactive && item.Work is IConcurrentAccountWork) : null;
                        if (read is not null)
                        {
                            queue.Remove(read);
                            _count--;
                            _activeReads.Add(account);
                            ready.Add(read);
                        }

                        if (!_active.Contains(account) && _active.Count < _workers)
                        {
                            List<ScheduledWork> serial = queue.Where(item => item.Work is not IConcurrentAccountWork ||
                                item.Priority != WorkPriority.Interactive).ToList();
                            if (serial.Count == 0)
                            {
                                _turns.Enqueue(account);
                                continue;
                            }
                            ScheduledWork work = _interactiveTurns < 3 ?
                                serial.OrderBy(item => item.Priority).First() :
                                serial.FirstOrDefault(item => item.Priority == WorkPriority.Reconciliation) ?? serial[0];
                            _interactiveTurns = work.Priority == WorkPriority.Interactive ? _interactiveTurns + 1 : 0;
                            queue.Remove(work);
                            _count--;
                            _active.Add(account);
                            ready.Add(work);
                        }

                        _turns.Enqueue(account);
                    }

                    foreach (ScheduledWork work in ready)
                    {
                        CancellationToken stop = _accountStops[work.Account].Token;
                        Task task = Task.Run(() => RunAsync(work, stop));
                        _running.Add(task);
                        Task? previous = _accountTasks.GetValueOrDefault(work.Account);
                        _accountTasks[work.Account] = previous is { IsCompleted: false } ? Task.WhenAll(previous, task) : task;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutdown observes all running work below.
        }
        finally
        {
            Task[] tasks;
            lock (_gate)
            {
                foreach (ScheduledWork work in _queues.Values.SelectMany(queue => queue))
                {
                    work.Completion.TrySetCanceled(_shutdown.Token);
                }

                tasks = _running.ToArray();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ScheduledWork work, CancellationToken accountStop)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            work.Cancellation,
            accountStop
        );
        try
        {
            Failure result = Failure.Unavailable;
            if (work.Priority != WorkPriority.Prefetch || _policy.AllowOptionalWork)
            {
                for (int attempt = 1; attempt <= _attemptLimit; attempt++)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    result = await work.Work.RunAsync(linked.Token).ConfigureAwait(false);
                    if (result != Failure.Transient || attempt == _attemptLimit)
                    {
                        break;
                    }
                    lock (_gate)
                    {
                        if (_paused)
                        {
                            break;
                        }
                    }

                    await Task.Delay(_retry.Delay(attempt), _time, linked.Token).ConfigureAwait(false);
                }
            }

            work.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            work.Completion.TrySetCanceled(linked.Token);
        }
        catch (Exception exception)
        {
            work.Completion.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (work.Priority == WorkPriority.Interactive && work.Work is IConcurrentAccountWork)
                {
                    _activeReads.Remove(work.Account);
                }
                else
                {
                    _active.Remove(work.Account);
                }
            }

            _signal.Writer.TryWrite(0);
        }
    }
}

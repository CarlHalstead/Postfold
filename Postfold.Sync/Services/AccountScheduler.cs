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
    private readonly List<Task> _running = new List<Task>();
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
                    _running.RemoveAll(task => task.IsCompleted);
                    int turns = _turns.Count;
                    while (turns-- > 0 && _active.Count < _workers && _turns.TryDequeue(out AccountId account))
                    {
                        if (!_queues.TryGetValue(account, out List<ScheduledWork>? queue))
                        {
                            continue;
                        }

                        if (queue.Count == 0 && !_active.Contains(account))
                        {
                            _queues.Remove(account);
                            continue;
                        }

                        if (!_active.Contains(account) && queue.Count > 0)
                        {
                            ScheduledWork work = _interactiveTurns < 3 ?
                                queue.OrderBy(item => item.Priority).First() :
                                queue.FirstOrDefault(item => item.Priority == WorkPriority.Reconciliation) ?? queue[0];
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
                        _running.Add(Task.Run(() => RunAsync(work)));
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

    private async Task RunAsync(ScheduledWork work)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            work.Cancellation
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
                _active.Remove(work.Account);
            }

            _signal.Writer.TryWrite(0);
        }
    }
}

using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Updates;

/// <summary>One application owner for scheduling, policy, cancellation and observed authenticated operations.</summary>
internal sealed class ScheduledClientUpdates : IClientUpdates, IClientUpdateActivity, IAsyncDisposable
{
    private readonly object _gate = new object();
    private readonly AuthenticatedClientUpdates _client;
    private readonly IDesktopPreferenceStore _store;
    private readonly IUpdateConnection _connection;
    private readonly TimeProvider _time;
    private readonly string _schedulePath;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
    private UpdatePreferences _preferences = UpdatePreferences.Default;
    private UpdateStatus _current = new UpdateStatus(UpdateStage.Idle);
    private Task _scheduler = Task.CompletedTask;
    private Task? _disposal;
    private Task<UpdateStatus>? _operation;
    private CancellationTokenSource? _cancellation;
    private bool _download;
    private bool _started;
    private bool _ready;
    private bool _stopped;
    private DateTimeOffset? _lastCheck;
    private DateTimeOffset _retryAt;
    private int _failures;
    private long _wakeVersion;
    private TaskCompletionSource _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ScheduledClientUpdates(
        AuthenticatedClientUpdates client,
        IDesktopPreferenceStore store,
        IUpdateConnection connection,
        TimeProvider time,
        string stateDirectory
    )
    {
        _client = client;
        _store = store;
        _connection = connection;
        _time = time;
        _schedulePath = Path.Combine(stateDirectory, "schedule.check");
    }

    public UpdateStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _operation is not null;
            }
        }
    }

    public bool IsCancelling
    {
        get
        {
            lock (_gate)
            {
                return _cancellation?.IsCancellationRequested == true;
            }
        }
    }

    public event EventHandler? Changed;

    public Task<UpdateStatus> CheckAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        return Begin(download: false, progress, cancellationToken);
    }

    public Task<UpdateStatus> DownloadAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        return Begin(download: true, progress, cancellationToken);
    }

    public Task<UpdateStatus> RestartAsync(CancellationToken cancellationToken)
    {
        return _client.RestartAsync(cancellationToken);
    }

    public void Configure(UpdatePreferences preferences)
    {
        if (preferences.CheckIntervalHours is not (24 or 168))
        {
            throw new ArgumentException("Only daily or weekly update checks are supported.", nameof(preferences));
        }
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            _preferences = preferences;
            if (_operation is not null && !Allowed(_download))
            {
                _cancellation!.Cancel();
            }
            Changed?.Invoke(this, EventArgs.Empty);
            Wake();
        }
    }

    public Task CancelAsync()
    {
        lock (_gate)
        {
            _cancellation?.Cancel();
            Changed?.Invoke(this, EventArgs.Empty);
            return _operation ?? Task.CompletedTask;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= StopAsync();
            return new ValueTask(_disposal);
        }
    }

    /// <summary>Application startup only; Settings construction must never call this method.</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started || _stopped)
            {
                throw new InvalidOperationException("Update scheduling can start only once.");
            }
            _started = true;
        }
        // Even a synchronously completing store implementation must not execute durable IO on the dispatcher.
        _preferences = (await Task.Run(() => _store.ReadDesktopPreferencesAsync(cancellationToken), cancellationToken).ConfigureAwait(false)).Updates;
        _lastCheck = await Task.Run(ReadSchedule, cancellationToken).ConfigureAwait(false);
        _connection.Changed += OnConnectionChanged;
        lock (_gate)
        {
            _ready = true;
            Changed?.Invoke(this, EventArgs.Empty);
            _scheduler = Task.Run(ScheduleAsync, CancellationToken.None);
        }
    }

    /// <summary>Activation/resume reevaluates elapsed time; it never resets the interval or bypasses backoff.</summary>
    internal Task ResumeAsync()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return Task.CompletedTask;
            }
            if (_settled.Task.IsCompleted)
            {
                _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Wake();
            return _settled.Task;
        }
    }

    private async Task StopAsync()
    {
        Task? operation;
        lock (_gate)
        {
            _stopped = true;
            _lifetime.Cancel();
            _cancellation?.Cancel();
            operation = _operation;
        }
        try
        {
            await _scheduler.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (operation is not null)
                {
                    await operation.ConfigureAwait(false);
                }
            }
            finally
            {
                _connection.Changed -= OnConnectionChanged;
                if (_connection is SystemUpdateConnection system)
                {
                    system.Dispose();
                }
                _client.Dispose();
            }
        }
    }

    private Task<UpdateStatus> Begin(bool download, IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopped || !_ready || _operation is not null)
            {
                return Task.FromResult(new UpdateStatus(UpdateStage.Failed, Failure: Failure.PolicyBlocked));
            }
            if (_time.GetUtcNow() < _retryAt)
            {
                return Task.FromResult(_current);
            }
            if (!Allowed(download) || download && _current.Stage != UpdateStage.Available)
            {
                _current = new UpdateStatus(UpdateStage.Failed, Failure: _connection.Current == UpdateConnection.Offline ? Failure.Unavailable : Failure.PolicyBlocked);
                Changed?.Invoke(this, EventArgs.Empty);
                return Task.FromResult(_current);
            }
            _download = download;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            CancellationTokenSource owner = _cancellation;
            owner.Token.Register(() =>
            {
                lock (_gate)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            });
            _current = new UpdateStatus(download ? UpdateStage.Downloading : UpdateStage.Checking, download ? _current.Version : null);
            if (!download)
            {
                _lastCheck = _time.GetUtcNow();
            }
            _operation = Task.Run(() => RunAsync(download, progress, owner), CancellationToken.None);
            Changed?.Invoke(this, EventArgs.Empty);
            return _operation;
        }
    }

    private async Task<UpdateStatus> RunAsync(bool download, IProgress<UpdateStatus> progress, CancellationTokenSource owner)
    {
        UpdateStatus result = new UpdateStatus(UpdateStage.Failed, Failure: Failure.Unavailable);
        try
        {
            try
            {
                IProgress<UpdateStatus> reports = new OperationProgress(this, owner, progress);
                result = download ? await _client.DownloadAsync(reports, owner.Token).ConfigureAwait(false) :
                    await _client.CheckAsync(reports, owner.Token).ConfigureAwait(false);
                if (owner.IsCancellationRequested)
                {
                    result = new UpdateStatus(UpdateStage.Cancelled);
                }
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested)
            {
                result = new UpdateStatus(UpdateStage.Cancelled);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = new UpdateStatus(UpdateStage.Failed, Failure: Failure.Unavailable);
            }
            lock (_gate)
            {
                SetRetry(result);
            }
            // This hint controls scheduling only. It grants no release, cache or trust authority.
            try
            {
                if (File.Exists(Path.Combine(Path.GetDirectoryName(_schedulePath)!, "trust.journal")))
                {
                    await Task.Run(WriteSchedule).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = new UpdateStatus(UpdateStage.Failed, Failure: Failure.Unavailable);
                lock (_gate)
                {
                    SetRetry(result);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (owner.IsCancellationRequested)
                {
                    result = new UpdateStatus(UpdateStage.Cancelled);
                    _retryAt = default;
                }
                result = result with
                {
                    RetryAt = _retryAt == default ? null : _retryAt
                };
                _current = result;
                _operation = null;
                _cancellation = null;
                owner.Dispose();
                Changed?.Invoke(this, EventArgs.Empty);
                Wake();
            }
        }
        return result;
    }

    private void SetRetry(UpdateStatus result)
    {
        if (result.Stage != UpdateStage.Failed)
        {
            _failures = 0;
            _retryAt = default;
            return;
        }
        _failures = Math.Min(_failures + 1, 8);
        _retryAt = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, _failures - 1)));
        if (_client.RetryAt > _retryAt)
        {
            _retryAt = _client.RetryAt;
        }
    }

    private bool Allowed(bool download)
    {
        return _preferences.ChecksEnabled && _connection.Current != UpdateConnection.Offline &&
            (!download || _preferences.DownloadsEnabled && (!_preferences.UnmeteredOnly || _connection.Current == UpdateConnection.Unmetered));
    }

    private async Task ScheduleAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TimeSpan delay = Timeout.InfiniteTimeSpan;
                bool check;
                long evaluated;
                lock (_gate)
                {
                    evaluated = _wakeVersion;
                    DateTimeOffset now = _time.GetUtcNow();
                    if (_lastCheck > now)
                    {
                        _lastCheck = now;
                    }
                    DateTimeOffset due = _retryAt != default ? _retryAt :
                        (_lastCheck ?? DateTimeOffset.MinValue).AddHours(_preferences.CheckIntervalHours);
                    check = _operation is null && Allowed(download: false) && now >= due;
                    if (_operation is null && Allowed(download: false) && !check)
                    {
                        delay = due - now;
                    }
                }
                if (check)
                {
                    UpdateStatus result = await CheckAsync(new OperationProgress(), _lifetime.Token).ConfigureAwait(false);
                    if (result.Stage == UpdateStage.Available)
                    {
                        lock (_gate)
                        {
                            check = Allowed(download: true) && !_stopped;
                        }
                        if (check)
                        {
                            await DownloadAsync(new OperationProgress(), _lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    continue;
                }
                using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                Task wake = _wake.WaitAsync(wait.Token);
                Task timer = Task.Delay(delay, _time, wait.Token);
                lock (_gate)
                {
                    if (_operation is null && evaluated == _wakeVersion)
                    {
                        _settled.TrySetResult();
                    }
                }
                await Task.WhenAny(wake, timer).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(wake, timer).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (wait.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _settled.TrySetResult();
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (_operation is not null && !Allowed(_download))
            {
                _cancellation!.Cancel();
            }
            Changed?.Invoke(this, EventArgs.Empty);
            Wake();
        }
    }

    private void Wake()
    {
        _wakeVersion++;
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private DateTimeOffset? ReadSchedule()
    {
        try
        {
            if (!File.Exists(_schedulePath) || new FileInfo(_schedulePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }
            byte[] bytes = new byte[20];
            using FileStream stream = File.OpenRead(_schedulePath);
            if (stream.Length != bytes.Length || stream.Read(bytes) != bytes.Length)
            {
                return null;
            }
            long ticks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
            DateTimeOffset now = _time.GetUtcNow();
            long retry = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
            int failures = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
            if (ticks < DateTimeOffset.MinValue.Ticks || ticks > now.Ticks || retry < 0 || retry > now.AddHours(1).Ticks || failures is < 0 or > 8)
            {
                return null;
            }
            _retryAt = retry == 0 ? default : new DateTimeOffset(retry, TimeSpan.Zero);
            _failures = failures;
            if (_retryAt != default)
            {
                _current = new UpdateStatus(UpdateStage.Failed, Failure: Failure.Transient)
                {
                    RetryAt = _retryAt
                };
            }
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteSchedule()
    {
        byte[] bytes = new byte[20];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, _lastCheck!.Value.Ticks);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), _retryAt.Ticks);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), _failures);
        string temporary = _schedulePath + "." + Guid.NewGuid().ToString("N") + ".partial";
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _schedulePath, overwrite: true);
    }

    private sealed class OperationProgress(
        ScheduledClientUpdates? service = null,
        CancellationTokenSource? owner = null,
        IProgress<UpdateStatus>? observer = null
    ) : IProgress<UpdateStatus>
    {
        public void Report(UpdateStatus value)
        {
            if (service is null)
            {
                return;
            }
            lock (service._gate)
            {
                if (service._cancellation != owner || owner!.IsCancellationRequested)
                {
                    return;
                }
                service._current = value;
                observer?.Report(value);
                service.Changed?.Invoke(service, EventArgs.Empty);
            }
        }
    }
}

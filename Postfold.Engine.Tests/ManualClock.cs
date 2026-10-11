using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly TaskCompletionSource<ManualTimer> _created = new TaskCompletionSource<ManualTimer>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<ManualTimer> TimerCreated => _created.Task;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new ManualTimer(callback, state);
        timer.Change(dueTime, period);
        _created.TrySetResult(timer);
        return timer;
    }

    internal void Advance(TimeSpan elapsed, ManualTimer timer)
    {
        _now += elapsed;
        timer.Advance(elapsed);
    }
}

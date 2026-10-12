using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Fakes;

internal sealed class ManualTimer : ITimer
{
    private readonly TimerCallback _callback;
    private readonly object? _state;

    internal ManualTimer(TimerCallback callback, object? state)
    {
        _callback = callback;
        _state = state;
    }

    internal TimeSpan Due { get; private set; } = Timeout.InfiniteTimeSpan;
    internal bool Disposed
    {
        get; private set;
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        Due = dueTime;
        return !Disposed;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Advance(TimeSpan elapsed)
    {
        if (Disposed || Due == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        Due -= elapsed;
        if (Due <= TimeSpan.Zero)
        {
            Due = Timeout.InfiniteTimeSpan;
            _callback(_state);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

/// <summary>Timers and elapsed policy time advance only by an explicit test action.</summary>
internal sealed class UpdatePolicyClock : TimeProvider
{
    private readonly object _gate = new object();
    private readonly List<ControlledTimer> _timers = [];
    private DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ControlledTimer timer = new ControlledTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        ControlledTimer[] due;
        lock (_gate)
        {
            _now += amount;
            due = _timers.Where(timer => timer.Due <= _now).ToArray();
            foreach (ControlledTimer timer in due)
            {
                timer.Due = DateTimeOffset.MaxValue;
            }
        }
        foreach (ControlledTimer timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    private sealed class ControlledTimer(UpdatePolicyClock clock, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset Due { get; set; }
        internal TimerCallback Callback => callback;
        internal object? State => state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (!clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

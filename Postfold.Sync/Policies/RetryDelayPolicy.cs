using Postfold.Core.Interfaces;
using System;

namespace Postfold.Sync.Policies;

public sealed class RetryDelayPolicy : IRetryDelayPolicy
{
    private readonly TimeSpan _maximum;
    private readonly TimeSpan _initial;

    public RetryDelayPolicy(TimeSpan initial, TimeSpan maximum)
    {
        if (initial <= TimeSpan.Zero || maximum < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(initial));
        }

        _initial = initial;
        _maximum = maximum;
    }

    public TimeSpan Delay(int attempt)
    {
        double bound = Math.Min(
            _maximum.TotalMilliseconds,
            _initial.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20))
        );
        return TimeSpan.FromMilliseconds(bound * (0.5 + Random.Shared.NextDouble() * 0.5));
    }
}

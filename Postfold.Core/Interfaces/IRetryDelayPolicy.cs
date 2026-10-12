using System;

namespace Postfold.Core.Interfaces;

/// <summary>Calculates retry delays independently of waiting, allowing callers to use controlled time and cancellation.</summary>
public interface IRetryDelayPolicy
{
    TimeSpan Delay(int attempt);
}

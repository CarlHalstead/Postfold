using System;

namespace Postfold.Core;

public interface IRetryDelayPolicy
{
    TimeSpan Delay(int attempt);
}

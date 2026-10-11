using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public enum PendingState
{
    None, Queued, InFlight, RetryScheduled, NeedsAuthentication, NeedsReview, Completed, Failed, Dismissed
}

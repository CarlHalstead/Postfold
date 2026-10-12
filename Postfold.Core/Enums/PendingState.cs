namespace Postfold.Core.Enums;

public enum PendingState
{
    None,
    Queued,
    InFlight,
    RetryScheduled,
    NeedsAuthentication,
    NeedsReview,
    Completed,
    Failed,
    Dismissed
}

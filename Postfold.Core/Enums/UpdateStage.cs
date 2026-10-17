namespace Postfold.Core.Enums;

/// <summary>Authenticated updater presentation state; inactive never implies a successful check.</summary>
public enum UpdateStage
{
    Inactive,
    Idle,
    Checking,
    Current,
    Available,
    Downloading,
    Verifying,
    Ready,
    RestartPreparing,
    RestartBlocked,
    Failed,
    Cancelled,
    RestartCommitted
}

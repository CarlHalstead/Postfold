using Postfold.Core.Enums;

namespace Postfold.Core.Models.Updates;

/// <summary>Immutable local UI status. A null percentage means progress is not known.</summary>
public sealed record UpdateStatus(UpdateStage Stage, string? Version = null, double? Percentage = null, Failure Failure = Failure.None)
{
    /// <summary>Only a separately qualified native handoff may grant restart capability.</summary>
    public bool CanRestart { get; init; }
}

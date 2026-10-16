using Postfold.Core.Enums;
using System;

namespace Postfold.Core.Models.Updates;

/// <summary>Immutable local UI status. A null percentage means progress is not known.</summary>
public sealed record UpdateStatus(UpdateStage Stage, string? Version = null, double? Percentage = null, Failure Failure = Failure.None)
{
    /// <summary>Only a separately qualified native handoff may grant restart capability.</summary>
    public bool CanRestart { get; init; }

    /// <summary>Earliest retry admission after a bounded transport/scheduler failure; null means no cooldown.</summary>
    public DateTimeOffset? RetryAt { get; init; }
}

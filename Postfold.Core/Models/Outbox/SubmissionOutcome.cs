using Postfold.Core.Enums;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Outbox;

public sealed record SubmissionOutcome(
    SubmissionState State,
    SubmissionPhase Phase,
    ImmutableArray<RecipientEvidence> Recipients,
    Failure Failure,
    string? Receipt = null
);

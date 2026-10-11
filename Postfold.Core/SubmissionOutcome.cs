using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record SubmissionOutcome(SubmissionState State, SubmissionPhase Phase,
    ImmutableArray<RecipientEvidence> Recipients, Failure Failure, string? Receipt = null);

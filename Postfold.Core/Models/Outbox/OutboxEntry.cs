using Postfold.Core.Enums;

namespace Postfold.Core.Models.Outbox;

public sealed record OutboxEntry(
    FrozenSubmission Submission,
    SubmissionState State,
    long Revision,
    SubmissionOutcome? Evidence
);

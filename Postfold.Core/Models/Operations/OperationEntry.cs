using Postfold.Core.Enums;
using System;

namespace Postfold.Core.Models.Operations;

public sealed record OperationEntry(
    MutationIntent Intent,
    PendingState State,
    long Revision,
    int Attempts,
    DateTimeOffset Due,
    Failure Evidence
);

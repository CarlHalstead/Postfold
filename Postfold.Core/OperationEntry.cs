using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record OperationEntry(MutationIntent Intent, PendingState State, long Revision,
    int Attempts, DateTimeOffset Due, Failure Evidence);

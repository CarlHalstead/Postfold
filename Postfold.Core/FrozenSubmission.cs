using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record FrozenSubmission(Guid Id, AccountId Account, BlobReference Mime,
    string Sender, ImmutableArray<string> Recipients, Guid? Predecessor = null, Guid? SourceDraft = null);

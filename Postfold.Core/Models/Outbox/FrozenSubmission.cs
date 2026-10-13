using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using System;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Outbox;

public sealed record FrozenSubmission(
    Guid Id,
    AccountId Account,
    BlobReference Mime,
    string Sender,
    ImmutableArray<string> Recipients,
    Guid? Predecessor = null,
    Guid? SourceDraft = null,
    string? Subject = null
);

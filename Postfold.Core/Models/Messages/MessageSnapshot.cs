using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using System;

namespace Postfold.Core.Models.Messages;

public sealed record MessageSnapshot(
    AccountId Account,
    MessageId Id,
    ProviderIdentity Provider,
    string Subject,
    string Participants,
    string? MessageIdHeader,
    DateTimeOffset Date,
    long Size,
    bool Read,
    bool Flagged,
    ContentAvailability Availability,
    PendingState Pending,
    string? NativeThreadId = null,
    string? CachedPreview = null,
    bool HasKnownAttachments = false
);

using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MessageSnapshot(
    AccountId Account, MessageId Id, ProviderIdentity Provider, string Subject, string Participants,
    string? MessageIdHeader, DateTimeOffset Date, long Size, bool Read, bool Flagged,
    ContentAvailability Availability, PendingState Pending, string? NativeThreadId = null);

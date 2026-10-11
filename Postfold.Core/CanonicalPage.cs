using System.Collections.Immutable;

namespace Postfold.Core;

public sealed record CanonicalPage(Checkpoint Checkpoint, ImmutableArray<MailboxSnapshot> Collections,
    ImmutableArray<MessageSnapshot> Messages, ImmutableArray<MembershipChange> Memberships,
    ImmutableArray<string> RemovedMessages, bool FullEnumeration);

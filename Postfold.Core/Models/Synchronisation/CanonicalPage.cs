using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Synchronisation;

public sealed record CanonicalPage(
    Checkpoint Checkpoint,
    ImmutableArray<MailboxSnapshot> Collections,
    ImmutableArray<MessageSnapshot> Messages,
    ImmutableArray<MembershipChange> Memberships,
    ImmutableArray<string> RemovedMessages,
    bool FullEnumeration
);

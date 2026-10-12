using System.Collections.Immutable;

namespace Postfold.Core;

public sealed record AccountSetupResult(Failure Credentials, Failure Incoming, Failure Outgoing,
    ImmutableArray<MailboxSnapshot> Mailboxes);

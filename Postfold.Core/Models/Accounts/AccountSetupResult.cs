using Postfold.Core.Enums;
using Postfold.Core.Models.Mailboxes;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Accounts;

public sealed record AccountSetupResult(
    Failure Credentials,
    Failure Incoming,
    Failure Outgoing,
    ImmutableArray<MailboxSnapshot> Mailboxes
);

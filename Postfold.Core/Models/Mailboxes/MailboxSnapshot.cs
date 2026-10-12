using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Mailboxes;

public sealed record MailboxSnapshot(
    AccountId Account,
    FolderId Id,
    string RemoteKey,
    string Name,
    MailboxRole Role,
    bool Selectable,
    bool Writable,
    uint UidValidity
);

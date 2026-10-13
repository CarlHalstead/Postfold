using Postfold.Core.Models.Accounts;

namespace Postfold.Core.Models.Mailboxes;

/// <summary>A local unread count for one account-scoped mailbox; it does not imply complete server coverage.</summary>
public sealed record MailboxUnreadCount(AccountId Account, FolderId Folder, long Count);

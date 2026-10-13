using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;

namespace Postfold.Core.Models.Search;

public sealed record SearchRequest(
    AccountId? Account,
    bool Unified,
    string Terms,
    FolderId? Folder,
    bool UnreadOnly,
    int Limit,
    string? Continuation = null,
    bool InboxOnly = false,
    bool KnownAttachmentsOnly = false
);

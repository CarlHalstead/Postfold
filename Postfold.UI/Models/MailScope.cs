using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;

namespace Postfold.UI.Models;

public sealed record MailScope(string Name, AccountId? Account, FolderId? Folder, bool InboxOnly, string? Group = null)
{
    public bool IsAccount => Account is not null && Folder is null;
    public bool IsFolder => Folder is not null;

    public override string ToString()
    {
        return Name;
    }
}

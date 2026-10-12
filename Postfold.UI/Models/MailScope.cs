using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;

namespace Postfold.UI.Models;

public sealed record MailScope(string Name, AccountId? Account, FolderId? Folder, bool InboxOnly)
{
    public override string ToString()
    {
        return Name;
    }
}

using Postfold.Core;

namespace Postfold.UI;

public sealed record MailScope(string Name, AccountId? Account, FolderId? Folder, bool InboxOnly)
{
    public override string ToString()
    {
        return Name;
    }
}

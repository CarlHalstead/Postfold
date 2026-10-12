using System;
using System.Linq;

namespace Postfold.Core;

public sealed record FolderRoles(string? Sent, string? Drafts, string? Archive, string? Trash)
{
    public bool IsValid => new[] { Sent, Drafts, Archive, Trash }.All(item => item is null ||
        item.Length is > 0 and <= 1024 && !item.Contains('\0')) && new[] { Sent, Drafts, Archive, Trash }
        .Where(item => item is not null).Distinct(StringComparer.Ordinal).Count() ==
        new[] { Sent, Drafts, Archive, Trash }.Count(item => item is not null);

    public MailboxSnapshot Apply(MailboxSnapshot mailbox)
    {
        MailboxRole role = mailbox.RemoteKey == Sent ? MailboxRole.Sent :
            mailbox.RemoteKey == Drafts ? MailboxRole.Drafts : mailbox.RemoteKey == Archive ? MailboxRole.Archive :
            mailbox.RemoteKey == Trash ? MailboxRole.Trash : mailbox.Role is MailboxRole.Sent or MailboxRole.Drafts or
                MailboxRole.Archive or MailboxRole.Trash ? MailboxRole.Unresolved : mailbox.Role;
        return mailbox with
        {
            Role = role
        };
    }
}

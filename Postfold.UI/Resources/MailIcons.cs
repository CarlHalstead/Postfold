using Avalonia.Media;
using Postfold.Core.Enums;

namespace Postfold.UI.Resources;

/// <summary>Local outline geometry shared by navigation and commands; no remote artwork is requested.</summary>
public static class MailIcons
{
    public static readonly Geometry Back = Geometry.Parse("M 7,2 L 1,8 L 7,14 M 1,8 L 15,8");
    public static readonly Geometry Add = Geometry.Parse("M 8,1 L 8,15 M 1,8 L 15,8");
    public static readonly Geometry Refresh = Geometry.Parse("M 14,6 A 6,6 0 1 0 14,10 M 14,1 L 14,6 L 9,6");
    public static readonly Geometry Rename = Geometry.Parse("M 2,10 L 11,1 L 15,5 L 6,14 L 1,15 Z M 9,3 L 13,7");
    public static readonly Geometry Close = Geometry.Parse("M 3,3 L 13,13 M 13,3 L 3,13");
    public static readonly Geometry Account = Geometry.Parse("M 8,1 A 7,7 0 1 1 7.99,1 M 5,6 A 3,3 0 1 0 11,6 M 3,13 Q 8,8 13,13");
    public static readonly Geometry Folder = Geometry.Parse("M 1,4 L 6,4 L 8,6 L 15,6 L 15,14 L 1,14 Z");
    public static readonly Geometry Inbox = Geometry.Parse("M 2,2 L 14,2 L 15,14 L 1,14 Z M 1,7 L 5,7 L 6,10 L 10,10 L 11,7 L 15,7");
    public static readonly Geometry Sent = Geometry.Parse("M 1,1 L 15,8 L 1,15 L 4,8 Z M 4,8 L 15,8");
    public static readonly Geometry Draft = Geometry.Parse("M 3,1 L 10,1 L 14,5 L 14,15 L 3,15 Z M 10,1 L 10,5 L 14,5");
    public static readonly Geometry Archive = Geometry.Parse("M 1,2 L 15,2 L 15,5 L 1,5 Z M 2,5 L 2,14 L 14,14 L 14,5 M 6,8 L 10,8");
    public static readonly Geometry Trash = Geometry.Parse("M 2,4 L 14,4 M 6,4 L 6,1 L 10,1 L 10,4 M 4,4 L 5,15 L 11,15 L 12,4 M 7,7 L 7,12 M 9,7 L 9,12");

    public static Geometry ForRole(MailboxRole role)
    {
        return role switch
        {
            MailboxRole.Inbox => Inbox,
            MailboxRole.Sent => Sent,
            MailboxRole.Drafts => Draft,
            MailboxRole.Archive => Archive,
            MailboxRole.Trash => Trash,
            _ => Folder
        };
    }
}

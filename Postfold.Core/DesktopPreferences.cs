namespace Postfold.Core;

public sealed record DesktopPreferences(bool TrayEnabled, bool NotificationsEnabled)
{
    public static readonly DesktopPreferences Default = new DesktopPreferences(false, true);
}

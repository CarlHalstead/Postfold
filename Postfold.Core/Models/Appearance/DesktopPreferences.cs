namespace Postfold.Core.Models.Appearance;

public sealed record DesktopPreferences(bool TrayEnabled, bool NotificationsEnabled)
{
    public static readonly DesktopPreferences Default = new DesktopPreferences(false, true);
}

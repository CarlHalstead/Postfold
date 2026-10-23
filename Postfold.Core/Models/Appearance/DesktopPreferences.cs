using Postfold.Core.Models.Updates;

namespace Postfold.Core.Models.Appearance;

public sealed record DesktopPreferences(bool TrayEnabled, bool NotificationsEnabled)
{
    public static readonly DesktopPreferences Default = new DesktopPreferences(false, true);
    public int MailPollIntervalSeconds { get; init; } = 30;
    public UpdatePreferences Updates { get; init; } = UpdatePreferences.Default;
}

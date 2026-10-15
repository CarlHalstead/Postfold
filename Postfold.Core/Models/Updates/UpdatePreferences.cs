namespace Postfold.Core.Models.Updates;

/// <summary>Download permission is separate from explicit safe-restart installation consent.</summary>
public sealed record UpdatePreferences(bool ChecksEnabled = true, bool DownloadsEnabled = true, bool UnmeteredOnly = true)
{
    public static readonly UpdatePreferences Default = new UpdatePreferences();

    /// <summary>Saved automatic check interval in hours; scheduling remains inactive until the updater is enabled.</summary>
    public int CheckIntervalHours { get; init; } = 24;
}

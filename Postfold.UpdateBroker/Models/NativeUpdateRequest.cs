namespace Postfold.UpdateBroker.Models;

/// <summary>
/// Trusted composition inputs delivered through the launcher's private pipe, never a discovered JSON file.
/// Executable digests must come from the authenticated installation inventory, not adjacent hash files.
/// </summary>
public sealed record NativeUpdateRequest(
    int OldProcessId,
    long OldProcessStartUtcTicks,
    string BrokerSha256,
    string HelperPath,
    string HelperSha256,
    string InstallRoot,
    string InstalledVersion,
    string InstalledManifestSha256,
    string Channel,
    int ProfileFormat,
    string Migration,
    string PinnedRoot,
    string TrustJournal,
    string ManifestSha256,
    byte[] Manifest,
    byte[] Signature,
    byte[] Feed,
    string PackagePath
)
{
    /// <summary>Explicit admitted profile for normal reopening; null preserves the apply-only U04 boundary.</summary>
    public string? RestartProfile { get; init; }
}

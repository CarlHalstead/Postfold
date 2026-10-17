namespace Postfold.Desktop.Models.Updates;

/// <summary>Trusted installed inventory, supplied only by explicitly controlled composition.</summary>
internal sealed record NativeRestartConfiguration(
    string BrokerPath,
    string BrokerSha256,
    string HelperPath,
    string HelperSha256,
    string InstallRoot,
    string InstalledManifestSha256
);

using System.Collections.Generic;

namespace Postfold.Storage.Models;

internal static class ProfileMigrationManifest
{
    // Qualified from reviewed EF SQL and real SQLite; tests regenerate and compare this guard.
    // Normal reopen must not construct an EF model or recreate an in-memory profile just to classify mail storage.
    internal static readonly IReadOnlyList<ProfileMigrationVersion> Versions =
    [
        new ProfileMigrationVersion(
            "20261014150754_InitialEfProfile",
            2,
            "2051055BF5A44E69B6D9411D23C9CBF3FBB60E13587DE3C779B66CCD5E29B2AE"
        ),
        new ProfileMigrationVersion(
            "20261023214228_DraftDeletionIntent",
            3,
            "AD1CA608F2D368DC103BA1B4F5B7F9072272DD8375844B0EDB5508FD6F95D6C7"
        )
    ];
}

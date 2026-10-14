namespace Postfold.Storage.Models;

/// <summary>A reviewed EF history prefix and its independent format and schema guards.</summary>
internal sealed record ProfileMigrationVersion(string Id, int Format, string SchemaHash);

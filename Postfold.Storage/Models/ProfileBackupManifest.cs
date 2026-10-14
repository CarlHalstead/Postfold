using System;
using System.Collections.Generic;

namespace Postfold.Storage.Models;

internal sealed record ProfileBackupManifest(int Version, string Id, DateTimeOffset CreatedUtc, string SourceMigration, int Format, string SchemaHash, IReadOnlyList<ProfileBackupFile> Files);

using System.Collections.Immutable;

namespace Postfold.Core.Models.Updates;

/// <summary>One authenticated full release. Origin migrations describe supported forward paths, not rollback.</summary>
public sealed record UpdateManifest(
    string KeyId,
    string Channel,
    long Sequence,
    string Version,
    string SourceCommit,
    string MinimumApp,
    int MinimumFormat,
    int MaximumFormat,
    int TargetFormat,
    ImmutableArray<string> OriginMigrations,
    string TargetMigration,
    UpdateArtifact Feed,
    UpdateArtifact Package
);

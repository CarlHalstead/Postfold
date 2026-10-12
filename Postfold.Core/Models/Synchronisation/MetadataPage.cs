using Postfold.Core.Models.Messages;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Synchronisation;

public sealed record MetadataPage(
    Checkpoint Checkpoint,
    ImmutableArray<MessageSnapshot> Messages,
    ImmutableArray<ProviderIdentity> Removed,
    bool FullEnumeration,
    ImmutableArray<ProviderIdentity> Seen = default
);

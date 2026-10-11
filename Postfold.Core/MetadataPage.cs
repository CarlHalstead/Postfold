using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MetadataPage(Checkpoint Checkpoint, ImmutableArray<MessageSnapshot> Messages,
    ImmutableArray<ProviderIdentity> Removed, bool FullEnumeration,
    ImmutableArray<ProviderIdentity> Seen = default);

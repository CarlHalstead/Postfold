using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record RemoteDraftResult(ProviderIdentity? Identity, string? Version,
    BlobReference? ConflictingContent, bool Uncertain, Failure Failure);

using Postfold.Core.Enums;
using Postfold.Core.Models.Messages;

namespace Postfold.Core.Models.Drafts;

public sealed record RemoteDraftResult(
    ProviderIdentity? Identity,
    string? Version,
    BlobReference? ConflictingContent,
    bool Uncertain,
    Failure Failure
);

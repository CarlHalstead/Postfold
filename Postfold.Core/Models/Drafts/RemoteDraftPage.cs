using System.Collections.Immutable;

namespace Postfold.Core.Models.Drafts;

/// <summary>A bounded page of remote identities, versions and protected MIME snapshots; no absence deletion is implied.</summary>
public sealed record RemoteDraftPage(ImmutableArray<RemoteDraftResult> Drafts, string? Next);

namespace Postfold.Core.Models.Updates;

/// <summary>Exact delivered representation, before any decoding, extraction or normalisation.</summary>
public sealed record UpdateArtifact(string Name, long Length, string Sha256);

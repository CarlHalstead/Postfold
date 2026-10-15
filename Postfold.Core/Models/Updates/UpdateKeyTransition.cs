namespace Postfold.Core.Models.Updates;

/// <summary>A predecessor-authorised successor, requiring the exact activation manifest before later releases.</summary>
public sealed record UpdateKeyTransition(
    string KeyId,
    string Channel,
    long Epoch,
    string Successor,
    long ActivationSequence,
    string ActivationVersion,
    string ActivationSha256
);

using Postfold.Core.Enums;
using Postfold.Core.Policies.Updates;
using System;
using System.Security.Cryptography;

namespace Postfold.Core.Models.Updates;

/// <summary>Immutable monotonic trust state. Persist the signed input before exposing an accepted successor.</summary>
public sealed record UpdateTrustState(
    string PublicKey,
    string Channel,
    long Epoch = 0,
    long Sequence = 0,
    string? Version = null,
    string? ManifestSha256 = null,
    UpdateKeyTransition? Pending = null
)
{
    /// <summary>Rejects replay, equivocation and version rollback; an identical release is idempotent.</summary>
    public Outcome<UpdateTrustState> AcceptManifest(ReadOnlyMemory<byte> bytes, ReadOnlySpan<byte> signature)
    {
        Outcome<UpdateManifest> verified = UpdateTrust.VerifyManifest(bytes, signature, PublicKey);
        if (!verified.IsSuccess || verified.Value!.Channel != Channel)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.Validation);
        }
        UpdateManifest manifest = verified.Value;
        string digest = Convert.ToHexString(SHA256.HashData(bytes.Span));
        if (manifest.Sequence < Sequence ||
            manifest.Sequence == Sequence && (digest != ManifestSha256 || Pending is not null) ||
            manifest.Sequence > Sequence && Version is not null &&
            UpdateVersion.Parse(manifest.Version).CompareTo(UpdateVersion.Parse(Version)) <= 0 ||
            Pending is not null && (manifest.Sequence != Pending.ActivationSequence ||
                manifest.Version != Pending.ActivationVersion || digest != Pending.ActivationSha256))
        {
            return Outcome<UpdateTrustState>.Fail(Failure.PolicyBlocked);
        }
        return Outcome<UpdateTrustState>.Success(this with
        {
            Sequence = manifest.Sequence,
            Version = manifest.Version,
            ManifestSha256 = digest,
            Pending = null
        });
    }

    /// <summary>Retires the predecessor immediately and holds the successor at its exact activation document.</summary>
    public Outcome<UpdateTrustState> AcceptTransition(ReadOnlyMemory<byte> bytes, ReadOnlySpan<byte> signature)
    {
        Outcome<UpdateKeyTransition> verified = UpdateTrust.VerifyTransition(bytes, signature, PublicKey);
        if (!verified.IsSuccess || verified.Value!.Channel != Channel)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.Validation);
        }
        UpdateKeyTransition transition = verified.Value;
        if (Pending is not null || Epoch == long.MaxValue || transition.Epoch != Epoch + 1 ||
            transition.ActivationSequence <= Sequence || Version is not null &&
            UpdateVersion.Parse(transition.ActivationVersion).CompareTo(UpdateVersion.Parse(Version)) <= 0)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.PolicyBlocked);
        }
        return Outcome<UpdateTrustState>.Success(this with
        {
            PublicKey = transition.Successor,
            Epoch = transition.Epoch,
            Pending = transition
        });
    }
}

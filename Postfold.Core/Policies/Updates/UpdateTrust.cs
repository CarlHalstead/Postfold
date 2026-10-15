using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Postfold.Core.Policies.Updates;

/// <summary>Framework-only v1 exact-byte authentication. No network key, algorithm or parser negotiation.</summary>
public static class UpdateTrust
{
    public const int SignatureLength = 384;
    public const int MaximumDocumentBytes = 65536;

    /// <summary>Validates canonical DER SPKI containing exactly one 3072-bit RSA public key.</summary>
    public static string KeyId(string publicKey)
    {
        if (publicKey.Length > 1024)
        {
            throw new CryptographicException("Invalid v1 update key.");
        }
        byte[] bytes = Convert.FromBase64String(publicKey);
        if (bytes.Length > 768)
        {
            throw new CryptographicException("Invalid v1 update key.");
        }
        using RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(bytes, out int read);
        if (read != bytes.Length || rsa.KeySize != 3072 ||
            !rsa.ExportParameters(false).Exponent.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }) ||
            !rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(bytes))
        {
            throw new CryptographicException("Invalid v1 update key.");
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Authentication always precedes UTF-8 decoding and schema parsing.</summary>
    public static Outcome<UpdateManifest> VerifyManifest(ReadOnlyMemory<byte> bytes, ReadOnlySpan<byte> signature, string publicKey)
    {
        if (bytes.Length is <= 0 or > MaximumDocumentBytes || signature.Length != SignatureLength)
        {
            return Outcome<UpdateManifest>.Fail(Failure.Validation);
        }
        bytes = bytes.ToArray();
        if (!Authenticate(bytes.Span, signature, publicKey))
        {
            return Outcome<UpdateManifest>.Fail(Failure.Validation);
        }
        Outcome<UpdateManifest> result = InspectManifest(bytes);
        return result.IsSuccess && result.Value!.KeyId == KeyId(publicKey) ? result : Outcome<UpdateManifest>.Fail(Failure.Validation);
    }

    /// <summary>Unsigned inspection is for the offline signer only; it does not establish client trust.</summary>
    public static Outcome<UpdateManifest> InspectManifest(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return Outcome<UpdateManifest>.Success(UpdateContract.Manifest(bytes));
        }
        catch (Exception exception) when (Malformed(exception))
        {
            return Outcome<UpdateManifest>.Fail(Failure.Validation);
        }
    }

    /// <summary>Authenticates with the pinned predecessor before inspecting the bounded successor declaration.</summary>
    public static Outcome<UpdateKeyTransition> VerifyTransition(ReadOnlyMemory<byte> bytes, ReadOnlySpan<byte> signature, string publicKey)
    {
        if (bytes.Length is <= 0 or > MaximumDocumentBytes || signature.Length != SignatureLength)
        {
            return Outcome<UpdateKeyTransition>.Fail(Failure.Validation);
        }
        bytes = bytes.ToArray();
        if (!Authenticate(bytes.Span, signature, publicKey))
        {
            return Outcome<UpdateKeyTransition>.Fail(Failure.Validation);
        }
        Outcome<UpdateKeyTransition> result = InspectTransition(bytes);
        return result.IsSuccess && result.Value!.KeyId == KeyId(publicKey) ? result : Outcome<UpdateKeyTransition>.Fail(Failure.Validation);
    }

    /// <summary>Unsigned structural inspection for the offline signer; not a trust decision.</summary>
    public static Outcome<UpdateKeyTransition> InspectTransition(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            UpdateKeyTransition transition = UpdateContract.Transition(bytes);
            UpdateContract.Require(KeyId(transition.Successor) != transition.KeyId);
            return Outcome<UpdateKeyTransition>.Success(transition);
        }
        catch (Exception exception) when (Malformed(exception) || exception is CryptographicException)
        {
            return Outcome<UpdateKeyTransition>.Fail(Failure.Validation);
        }
    }

    /// <summary>Forward migration is supported when the signed origin and format range include the current profile.</summary>
    public static bool Compatible(UpdateManifest manifest, string channel, string installedVersion, int profileFormat, string migration)
    {
        UpdateVersion installed = UpdateVersion.Parse(installedVersion);
        return manifest.Channel == channel && UpdateVersion.Parse(manifest.MinimumApp).CompareTo(installed) <= 0 &&
            UpdateVersion.Parse(manifest.Version).CompareTo(installed) > 0 &&
            profileFormat >= manifest.MinimumFormat && profileFormat <= manifest.MaximumFormat &&
            manifest.OriginMigrations.Contains(migration);
    }

    private static bool Authenticate(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature, string publicKey)
    {
        if (bytes.Length is <= 0 or > MaximumDocumentBytes || signature.Length != SignatureLength || publicKey.Length > 1024)
        {
            return false;
        }
        try
        {
            KeyId(publicKey);
            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static bool Malformed(Exception exception)
    {
        return exception is InvalidDataException or JsonException or DecoderFallbackException or FormatException or
            OverflowException or InvalidOperationException or KeyNotFoundException or ArgumentException;
    }
}

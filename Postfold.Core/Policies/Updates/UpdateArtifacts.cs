using Postfold.Core.Enums;
using Postfold.Core.Models.Updates;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Policies.Updates;

/// <summary>Bounded full-artifact and generated-feed inspection shared by clients and the offline signer.</summary>
public static class UpdateArtifacts
{
    /// <summary>Reads to EOF, checking the actual delivered byte count rather than Content-Length.</summary>
    public static async Task<Failure> VerifyAsync(Stream stream, UpdateArtifact artifact, CancellationToken cancellationToken)
    {
        return await CopyVerifiedAsync(stream, Stream.Null, artifact, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies only bounded bytes; callers promote the destination only after successful verification.</summary>
    public static async Task<Failure> CopyVerifiedAsync(Stream stream, Stream destination, UpdateArtifact artifact, CancellationToken cancellationToken)
    {
        if (artifact.Length is <= 0 or > UpdateContract.MaximumPackageBytes ||
            !UpdateContract.Matches(artifact.Sha256, "[A-F0-9]{64}"))
        {
            return Failure.Validation;
        }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            length += read;
            if (length > artifact.Length)
            {
                return Failure.Validation;
            }
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return length == artifact.Length && Convert.ToHexString(hash.GetHashAndReset()) == artifact.Sha256 ?
            Failure.None : Failure.Validation;
    }

    /// <summary>Authenticate exact feed bytes before this strict structural pass and before any Velopack parser.</summary>
    public static Failure VerifyFeed(ReadOnlyMemory<byte> bytes, UpdateManifest manifest)
    {
        if (bytes.Length != manifest.Feed.Length || bytes.Length > UpdateContract.MaximumFeedBytes)
        {
            return Failure.Validation;
        }
        bytes = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes.Span)) != manifest.Feed.Sha256)
        {
            return Failure.Validation;
        }
        try
        {
            using JsonDocument document = UpdateContract.Document(bytes, UpdateContract.MaximumFeedBytes);
            JsonElement root = document.RootElement;
            UpdateContract.Fields(root, "Assets");
            JsonElement assets = root.GetProperty("Assets");
            UpdateContract.Require(assets.ValueKind == JsonValueKind.Array && assets.GetArrayLength() == 1);
            JsonElement asset = assets[0];
            string[] required = ["PackageId", "Version", "Type", "FileName", "SHA256", "Size"];
            string[] allowed = [.. required, "SHA1", "NotesMarkdown", "NotesHTML"];
            UpdateContract.Require(asset.ValueKind == JsonValueKind.Object &&
                required.All(name => asset.TryGetProperty(name, out _)) &&
                asset.EnumerateObject().All(property => allowed.Contains(property.Name)));
            UpdateContract.Require(UpdateContract.Text(asset, "PackageId") == "Postfold" &&
                UpdateContract.Text(asset, "Version") == manifest.Version &&
                UpdateContract.Text(asset, "Type") == "Full" &&
                UpdateContract.Text(asset, "FileName") == manifest.Package.Name &&
                UpdateContract.Text(asset, "SHA256") == manifest.Package.Sha256 &&
                asset.GetProperty("Size").TryGetInt64(out long size) && size == manifest.Package.Length);
            foreach (string name in new[] { "SHA1", "NotesMarkdown", "NotesHTML" })
            {
                if (asset.TryGetProperty(name, out JsonElement optional))
                {
                    UpdateContract.Require(optional.ValueKind is JsonValueKind.String or JsonValueKind.Null);
                }
            }
            return Failure.None;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return Failure.Validation;
        }
    }
}

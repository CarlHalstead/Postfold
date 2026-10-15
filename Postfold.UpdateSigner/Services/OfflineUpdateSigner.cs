using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Postfold.UpdateSigner.Services;

/// <summary>Offline exact-byte signer. Keys are encrypted PKCS#8 inputs; no generation, network or overwrite path.</summary>
public static class OfflineUpdateSigner
{
    /// <summary>Inspects exact inputs and emits a new signature, returning false on expected validation or IO failure.</summary>
    public static async Task<bool> SignAsync(
        string documentPath,
        string artifactDirectory,
        string keyPath,
        string signaturePath,
        ReadOnlyMemory<char> password,
        bool transition,
        CancellationToken cancellationToken
    )
    {
        byte[]? encrypted = null;
        try
        {
            byte[] document = await ReadBoundedAsync(documentPath, UpdateTrust.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
            if (transition)
            {
                Outcome<UpdateKeyTransition> rotation = UpdateTrust.InspectTransition(document);
                if (!rotation.IsSuccess || !await InspectActivationAsync(rotation.Value!, artifactDirectory, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else
            {
                Outcome<UpdateManifest> inspected = UpdateTrust.InspectManifest(document);
                if (!inspected.IsSuccess || !await InspectArtifactsAsync(inspected.Value!, artifactDirectory, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            encrypted = await ReadBoundedAsync(keyPath, 16384, cancellationToken).ConfigureAwait(false);
            using RSA key = RSA.Create();
            key.ImportEncryptedPkcs8PrivateKey(password.Span, encrypted, out int consumed);
            if (consumed != encrypted.Length || key.KeySize != 3072)
            {
                return false;
            }
            string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            byte[] signature = key.SignData(document, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            bool valid = transition ? UpdateTrust.VerifyTransition(document, signature, publicKey).IsSuccess :
                UpdateTrust.VerifyManifest(document, signature, publicKey).IsSuccess;
            if (!valid)
            {
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream output = new FileStream(signaturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(signature);
            output.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or XmlException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum)
        {
            throw new InvalidDataException("Bounded signing input rejected.");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Signing input changed size.");
        }
        return bytes;
    }

    private static async Task<bool> InspectActivationAsync(UpdateKeyTransition transition, string directory, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(Path.Combine(directory, "activation.manifest.json"), UpdateTrust.MaximumDocumentBytes, cancellationToken).ConfigureAwait(false);
        byte[] signature = await ReadBoundedAsync(Path.Combine(directory, "activation.manifest.sig"), UpdateTrust.SignatureLength, cancellationToken).ConfigureAwait(false);
        Outcome<UpdateManifest> verified = UpdateTrust.VerifyManifest(bytes, signature, transition.Successor);
        return verified.IsSuccess && Convert.ToHexString(SHA256.HashData(bytes)) == transition.ActivationSha256 &&
            verified.Value!.Sequence == transition.ActivationSequence && verified.Value.Version == transition.ActivationVersion &&
            verified.Value.Channel == transition.Channel &&
            await InspectArtifactsAsync(verified.Value, directory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> InspectArtifactsAsync(UpdateManifest manifest, string directory, CancellationToken cancellationToken)
    {
        byte[] feed = await ReadBoundedAsync(Path.Combine(directory, manifest.Feed.Name), 262144, cancellationToken).ConfigureAwait(false);
        if (UpdateArtifacts.VerifyFeed(feed, manifest) != Failure.None)
        {
            return false;
        }
        using FileStream package = new FileStream(Path.Combine(directory, manifest.Package.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (await UpdateArtifacts.VerifyAsync(package, manifest.Package, cancellationToken).ConfigureAwait(false) != Failure.None)
        {
            return false;
        }
        package.Position = 0;
        using ZipArchive archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        ZipArchiveEntry[] specifications = archive.Entries.Where(entry => !entry.FullName.Contains('/') &&
            entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
        if (specifications.Length != 1 || specifications[0].Length is <= 0 or > 65536)
        {
            return false;
        }
        using Stream specification = specifications[0].Open();
        using XmlReader reader = XmlReader.Create(specification, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 65536
        });
        XDocument metadata = XDocument.Load(reader);
        if (metadata.Root?.Name.LocalName != "package")
        {
            return false;
        }
        XElement? element = metadata.Root?.Elements().SingleOrDefault(item => item.Name.LocalName == "metadata");
        return element is not null &&
            element.Elements().SingleOrDefault(item => item.Name.LocalName == "id")?.Value == "Postfold" &&
            element.Elements().SingleOrDefault(item => item.Name.LocalName == "version")?.Value == manifest.Version;
    }
}

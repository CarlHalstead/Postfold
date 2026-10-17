using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Stores;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Postfold.Desktop.Services.Updates;

/// <summary>
/// One authenticated immutable full release. Owns staging instead of calling DownloadUpdatesAsync,
/// whose extraction/finally and existing-cache shortcuts are outside the source callback's control.
/// </summary>
internal sealed class VerifiedUpdateSource : IUpdateSource
{
    private readonly IUpdateTransport _transport;
    private readonly ImmutableArray<byte> _feed;
    private readonly string _cache;
    private readonly SemaphoreSlim _downloads = new SemaphoreSlim(1, 1);

    private VerifiedUpdateSource(IUpdateTransport transport, UpdateManifest manifest, byte[] feed, string cache, byte[] document, byte[] signature)
    {
        _transport = transport;
        Manifest = manifest;
        _feed = [.. feed];
        _cache = Path.GetFullPath(cache);
        ManifestSha256 = Convert.ToHexString(SHA256.HashData(document));
        Document = [.. document];
        Signature = [.. signature];
    }

    internal UpdateManifest Manifest { get; }
    internal string ManifestSha256 { get; }
    internal ImmutableArray<byte> Document { get; }
    internal ImmutableArray<byte> Signature { get; }
    internal ImmutableArray<byte> Feed => _feed;

    public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != "Postfold" || channel != "win-x64-" + Manifest.Channel)
        {
            throw new InvalidDataException("Update source identity mismatch.");
        }
        // Each consumer receives a fresh library object; mutation cannot change the trusted snapshot.
        return Task.FromResult(VelopackAssetFeed.FromJson(Encoding.UTF8.GetString(_feed.AsSpan())));
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        if (releaseEntry.PackageId != "Postfold" || releaseEntry.Version.ToString() != Manifest.Version ||
            releaseEntry.Type != VelopackAssetType.Full || releaseEntry.FileName != Manifest.Package.Name ||
            releaseEntry.Size != Manifest.Package.Length || releaseEntry.SHA256 != Manifest.Package.Sha256)
        {
            throw new InvalidDataException("Only the immutable signed full release may be downloaded.");
        }
        Outcome<string> result = await DownloadFullAsync(localFile, cancelToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new IOException("Verified update download failed: " + result.Failure);
        }
        progress(100);
    }

    internal static async Task<Outcome<VerifiedUpdateSource>> OpenAsync(
        IUpdateTransport transport,
        UpdateTrustStore trust,
        string installedVersion,
        int profileFormat,
        string migration,
        string cache,
        CancellationToken cancellationToken
    )
    {
        Outcome<byte[]> fetched = await transport.FetchAsync("Postfold.win-x64-" + trust.State.Channel + ".manifest.json", 65536, cancellationToken).ConfigureAwait(false);
        Outcome<byte[]> signed = await transport.FetchAsync("Postfold.win-x64-" + trust.State.Channel + ".manifest.sig", UpdateTrust.SignatureLength, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || !signed.IsSuccess)
        {
            return Outcome<VerifiedUpdateSource>.Fail(!fetched.IsSuccess ? fetched.Failure : signed.Failure);
        }
        for (int link = 0; link <= 16; link++)
        {
            if (trust.State.Pending is UpdateKeyTransition pending)
            {
                string identity = "activation-" + pending.ActivationSha256;
                Failure activated = await AcceptDocumentAsync(transport, trust, identity, transition: false, cancellationToken).ConfigureAwait(false);
                if (activated != Failure.None)
                {
                    return Outcome<VerifiedUpdateSource>.Fail(activated);
                }
            }
            Outcome<UpdateManifest> verified = UpdateTrust.VerifyManifest(fetched.Value!, signed.Value!, trust.State.PublicKey);
            if (verified.IsSuccess)
            {
                UpdateManifest manifest = verified.Value!;
                if (!UpdateTrust.Compatible(manifest, trust.State.Channel, installedVersion, profileFormat, migration))
                {
                    return Outcome<VerifiedUpdateSource>.Fail(Failure.PolicyBlocked);
                }
                Outcome<byte[]> feed = await transport.FetchAsync(manifest.Feed.Name, 262144, cancellationToken).ConfigureAwait(false);
                if (!feed.IsSuccess || UpdateArtifacts.VerifyFeed(feed.Value!, manifest) != Failure.None)
                {
                    return Outcome<VerifiedUpdateSource>.Fail(feed.IsSuccess ? Failure.Validation : feed.Failure);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Outcome<UpdateTrustState> accepted = await Task.Run(() => trust.Accept(fetched.Value!, signed.Value!), cancellationToken).ConfigureAwait(false);
                return accepted.IsSuccess ? Outcome<VerifiedUpdateSource>.Success(new VerifiedUpdateSource(transport, manifest, feed.Value!, cache, fetched.Value!, signed.Value!)) :
                    Outcome<VerifiedUpdateSource>.Fail(accepted.Failure);
            }
            if (link == 16)
            {
                return Outcome<VerifiedUpdateSource>.Fail(Failure.Validation);
            }
            Failure rotated = await AcceptDocumentAsync(transport, trust, "transition-" + UpdateTrust.KeyId(trust.State.PublicKey), transition: true, cancellationToken).ConfigureAwait(false);
            if (rotated != Failure.None)
            {
                return Outcome<VerifiedUpdateSource>.Fail(rotated);
            }
        }
        return Outcome<VerifiedUpdateSource>.Fail(Failure.Validation);
    }

    /// <summary>Independently rechecks existing files and atomically promotes only exact freshly verified bytes.</summary>
    internal Task<Outcome<string>> DownloadFullAsync(string path, CancellationToken cancellationToken)
    {
        return DownloadFullAsync(path, null, cancellationToken);
    }

    internal async Task<Outcome<string>> DownloadFullAsync(string path, Action? verifying, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(path);
        if (full != Path.Combine(_cache, Manifest.Package.Name) && full != Path.Combine(_cache, Manifest.Package.Name + ".partial"))
        {
            return Outcome<string>.Fail(Failure.Validation);
        }
        await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyPath(full);
            Directory.CreateDirectory(_cache);
            if (File.Exists(full))
            {
                verifying?.Invoke();
                using FileStream cached = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                Failure checkedCache = await UpdateArtifacts.VerifyAsync(cached, Manifest.Package, cancellationToken).ConfigureAwait(false);
                return checkedCache == Failure.None ? Outcome<string>.Success(full) : Outcome<string>.Fail(Failure.Corrupt);
            }
            long retained = 0;
            int files = 0;
            foreach (string file in Directory.EnumerateFiles(_cache))
            {
                VerifyPath(file);
                long length = new FileInfo(file).Length;
                if (++files > 256 || length > 2147483648L - Manifest.Package.Length - retained)
                {
                    return Outcome<string>.Fail(Failure.Unavailable);
                }
                retained += length;
            }
            string temporary = Path.Combine(_cache, Guid.NewGuid().ToString("N") + ".partial");
            using (FileStream staging = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                Failure downloaded = await _transport.DownloadAsync(Manifest.Package, staging, cancellationToken).ConfigureAwait(false);
                if (downloaded != Failure.None)
                {
                    return Outcome<string>.Fail(downloaded);
                }
                staging.Position = 0;
                verifying?.Invoke();
                Failure verified = await UpdateArtifacts.VerifyAsync(staging, Manifest.Package, cancellationToken).ConfigureAwait(false);
                if (verified != Failure.None)
                {
                    return Outcome<string>.Fail(verified);
                }
                staging.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            VerifyPath(full);
            File.Move(temporary, full, overwrite: false);
            return Outcome<string>.Success(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Outcome<string>.Fail(Failure.Unavailable);
        }
        finally
        {
            _downloads.Release();
        }
    }

    private static async Task<Failure> AcceptDocumentAsync(IUpdateTransport transport, UpdateTrustStore trust, string identity, bool transition, CancellationToken cancellationToken)
    {
        Outcome<byte[]> document = await transport.FetchAsync(identity + ".json", 65536, cancellationToken).ConfigureAwait(false);
        Outcome<byte[]> signature = await transport.FetchAsync(identity + ".sig", UpdateTrust.SignatureLength, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess || !signature.IsSuccess)
        {
            return !document.IsSuccess ? document.Failure : signature.Failure;
        }
        return (await Task.Run(() => trust.Accept(document.Value!, signature.Value!, transition), cancellationToken).ConfigureAwait(false)).Failure;
    }

    private static void VerifyPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Update cache paths must not use links.");
            }
        }
    }
}

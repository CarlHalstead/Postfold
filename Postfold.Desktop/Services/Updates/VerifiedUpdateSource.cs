using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Stores;
using Postfold.UpdateBroker.Services;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        Outcome<VerifiedUpdateSource?> discovered = await DiscoverAsync(transport, trust, installedVersion, profileFormat, migration, cache, cancellationToken).ConfigureAwait(false);
        return discovered.IsSuccess && discovered.Value is not null ? Outcome<VerifiedUpdateSource>.Success(discovered.Value) :
            Outcome<VerifiedUpdateSource>.Fail(discovered.IsSuccess ? Failure.PolicyBlocked : discovered.Failure);
    }

    /// <summary>A successful null selection means the authenticated release is already installed, never a transport or trust refusal.</summary>
    internal static async Task<Outcome<VerifiedUpdateSource?>> DiscoverAsync(
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
            return Outcome<VerifiedUpdateSource?>.Fail(!fetched.IsSuccess ? fetched.Failure : signed.Failure);
        }
        for (int link = 0; link <= 16; link++)
        {
            if (trust.State.Pending is UpdateKeyTransition pending)
            {
                string identity = "activation-" + pending.ActivationSha256;
                Failure activated = await AcceptDocumentAsync(transport, trust, identity, transition: false, cancellationToken).ConfigureAwait(false);
                if (activated != Failure.None)
                {
                    return Outcome<VerifiedUpdateSource?>.Fail(activated);
                }
            }
            Outcome<UpdateManifest> verified = UpdateTrust.VerifyManifest(fetched.Value!, signed.Value!, trust.State.PublicKey);
            if (verified.IsSuccess)
            {
                UpdateManifest manifest = verified.Value!;
                bool current = manifest.Version == installedVersion && manifest.Channel == trust.State.Channel &&
                    UpdateVersion.Parse(manifest.MinimumApp).CompareTo(UpdateVersion.Parse(installedVersion)) <= 0 &&
                    profileFormat >= manifest.MinimumFormat && profileFormat <= manifest.MaximumFormat &&
                    manifest.OriginMigrations.Contains(migration);
                if (!current && !UpdateTrust.Compatible(manifest, trust.State.Channel, installedVersion, profileFormat, migration))
                {
                    return Outcome<VerifiedUpdateSource?>.Fail(Failure.PolicyBlocked);
                }
                Outcome<byte[]> feed = await transport.FetchAsync(manifest.Feed.Name, 262144, cancellationToken).ConfigureAwait(false);
                if (!feed.IsSuccess || UpdateArtifacts.VerifyFeed(feed.Value!, manifest) != Failure.None)
                {
                    return Outcome<VerifiedUpdateSource?>.Fail(feed.IsSuccess ? Failure.Validation : feed.Failure);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Outcome<UpdateTrustState> accepted = await Task.Run(() => trust.Accept(fetched.Value!, signed.Value!), cancellationToken).ConfigureAwait(false);
                return accepted.IsSuccess ? Outcome<VerifiedUpdateSource?>.Success(current ? null : new VerifiedUpdateSource(transport, manifest, feed.Value!, cache, fetched.Value!, signed.Value!)) :
                    Outcome<VerifiedUpdateSource?>.Fail(accepted.Failure);
            }
            if (link == 16)
            {
                return Outcome<VerifiedUpdateSource?>.Fail(Failure.Validation);
            }
            Failure rotated = await AcceptDocumentAsync(transport, trust, "transition-" + UpdateTrust.KeyId(trust.State.PublicKey), transition: true, cancellationToken).ConfigureAwait(false);
            if (rotated != Failure.None)
            {
                return Outcome<VerifiedUpdateSource?>.Fail(rotated);
            }
        }
        return Outcome<VerifiedUpdateSource?>.Fail(Failure.Validation);
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
        string? bundlePath = null;
        string? packagePath = null;
        FileStream? lease = null;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken operationToken = deadline.Token;
        try
        {
            VerifyPath(full);
            Directory.CreateDirectory(_cache);
            string leasePath = Path.Combine(_cache, "staging.lock");
            VerifyPath(leasePath);
            if (!File.Exists(leasePath))
            {
                // Reserve the lease itself before creating it, as well as the two scratch slots.
                int existing = 0;
                foreach (string entry in Directory.EnumerateFileSystemEntries(_cache))
                {
                    operationToken.ThrowIfCancellationRequested();
                    if (++existing >= 254)
                    {
                        return Outcome<string>.Fail(Failure.Unavailable);
                    }
                }
            }
            lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(full))
            {
                verifying?.Invoke();
                using FileStream cached = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                Failure checkedCache = await UpdateArtifacts.VerifyAsync(cached, Manifest.Package, operationToken).ConfigureAwait(false);
                if (checkedCache == Failure.None)
                {
                    cached.Position = 0;
                    NativeUpdateBroker.VerifyPackageIdentity(cached, Manifest.Version);
                }
                operationToken.ThrowIfCancellationRequested();
                return checkedCache == Failure.None ? Outcome<string>.Success(full) : Outcome<string>.Fail(Failure.Corrupt);
            }
            long retained = 0;
            int files = 0;
            long reserve = checked(UpdateBundle.MaximumBytes + Manifest.Package.Length);
            foreach (string file in Directory.EnumerateFileSystemEntries(_cache))
            {
                VerifyPath(file);
                if (Directory.Exists(file))
                {
                    return Outcome<string>.Fail(Failure.Unavailable);
                }
                long length = new FileInfo(file).Length;
                if (++files > 254 || length > 2147483648L - reserve - retained)
                {
                    return Outcome<string>.Fail(Failure.Unavailable);
                }
                retained += length;
            }
            if (new DriveInfo(Path.GetPathRoot(_cache)!).AvailableFreeSpace < reserve)
            {
                return Outcome<string>.Fail(Failure.Unavailable);
            }
            string bundleTemporary = Path.Combine(_cache, Guid.NewGuid().ToString("N") + ".bundle.partial");
            string packageTemporary = Path.Combine(_cache, Guid.NewGuid().ToString("N") + ".package.partial");
            using (FileStream bundle = new FileStream(bundleTemporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                bundlePath = bundleTemporary;
                Failure downloaded = await _transport.DownloadBundleAsync("Postfold-" + Manifest.Version + "-win-x64.zip", UpdateBundle.MaximumBytes, bundle, operationToken).ConfigureAwait(false);
                if (downloaded != Failure.None)
                {
                    return Outcome<string>.Fail(downloaded);
                }
                bundle.Position = 0;
                verifying?.Invoke();
                using (FileStream staging = new FileStream(packageTemporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    packagePath = packageTemporary;
                    Failure verified = await UpdateBundle.StageAsync(bundle, staging, this, operationToken).ConfigureAwait(false);
                    if (verified != Failure.None)
                    {
                        return Outcome<string>.Fail(verified);
                    }
                    await staging.FlushAsync(operationToken).ConfigureAwait(false);
                    staging.Flush(flushToDisk: true);
                    staging.Position = 0;
                    if (await UpdateArtifacts.VerifyAsync(staging, Manifest.Package, operationToken).ConfigureAwait(false) != Failure.None)
                    {
                        return Outcome<string>.Fail(Failure.Validation);
                    }
                    operationToken.ThrowIfCancellationRequested();
                    staging.Position = 0;
                    NativeUpdateBroker.VerifyPackageIdentity(staging, Manifest.Version);
                    operationToken.ThrowIfCancellationRequested();
                }
            }
            operationToken.ThrowIfCancellationRequested();
            VerifyPath(full);
            VerifyPath(packageTemporary);
            File.Move(packageTemporary, full, overwrite: false);
            packagePath = null;
            return Outcome<string>.Success(full);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome<string>.Fail(Failure.Unavailable);
        }
        catch (InvalidDataException)
        {
            return Outcome<string>.Fail(Failure.Validation);
        }
        catch (XmlException)
        {
            return Outcome<string>.Fail(Failure.Validation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Outcome<string>.Fail(Failure.Unavailable);
        }
        finally
        {
            try
            {
                RemoveOwnedScratch(bundlePath, packagePath);
            }
            finally
            {
                lease?.Dispose();
                _downloads.Release();
            }
        }
    }

    private static void RemoveOwnedScratch(string? bundle, string? package)
    {
        Exception? failure = null;
        foreach (string? path in new[] { bundle, package })
        {
            if (path is null)
            {
                continue;
            }
            try
            {
                VerifyPath(path);
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }
        }
        if (failure is not null)
        {
            throw new IOException("Owned update scratch cleanup failed; retained files remain accounted.", failure);
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

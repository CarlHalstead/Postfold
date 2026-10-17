using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Stores;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Updates;

/// <summary>
/// Explicit discovery and full-package staging, with optional owned safe restart admission.
/// Each operation reopens and replays user-owned trust before accepting or reusing a release.
/// </summary>
internal sealed class AuthenticatedClientUpdates : IClientUpdates, IDisposable
{
    private readonly HttpsUpdateTransport _transport;
    private readonly string _root;
    private readonly string _channel;
    private readonly string _installedVersion;
    private readonly int _profileFormat;
    private readonly string _migration;
    private readonly string _stateDirectory;
    private readonly IUpdateRestart? _restart;
    private int _active;
    private bool _disposed;
    private VerifiedUpdateSource? _source;
    private UpdateStatus _current = new UpdateStatus(UpdateStage.Idle);

    internal AuthenticatedClientUpdates(
        HttpsUpdateTransport transport,
        string pinnedRoot,
        string channel,
        string installedVersion,
        int profileFormat,
        string migration,
        string stateDirectory,
        IUpdateRestart? restart = null
    )
    {
        UpdateTrust.KeyId(pinnedRoot);
        UpdateVersion.Parse(installedVersion);
        if (channel is not ("stable" or "beta") || profileFormat < 2 || string.IsNullOrWhiteSpace(migration))
        {
            throw new ArgumentException("Exact admitted installation/profile compatibility is required.");
        }
        _transport = transport;
        _root = pinnedRoot;
        _channel = channel;
        _installedVersion = installedVersion;
        _profileFormat = profileFormat;
        _migration = migration;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _restart = restart;
        string installation = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (_stateDirectory.Equals(installation, StringComparison.OrdinalIgnoreCase) ||
            _stateDirectory.StartsWith(installation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Update state must be outside the installation.", nameof(stateDirectory));
        }
    }

    public UpdateStatus Current => Volatile.Read(ref _current);

    internal DateTimeOffset RetryAt => _transport.RetryAt;

    public Task<UpdateStatus> CheckAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        return RunAsync(download: false, progress, cancellationToken);
    }

    public Task<UpdateStatus> DownloadAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        return RunAsync(download: true, progress, cancellationToken);
    }

    public async Task<UpdateStatus> RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_restart is null || _source is null || !Current.CanRestart || Interlocked.CompareExchange(ref _active, 2, 0) != 0)
        {
            return new UpdateStatus(UpdateStage.RestartBlocked, Failure: Failure.PolicyBlocked);
        }
        try
        {
            Volatile.Write(ref _current, new UpdateStatus(UpdateStage.RestartPreparing, _source.Manifest.Version));
            UpdateStatus result = await _restart.RestartAsync(_source, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, result);
            return result;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    public void Dispose()
    {
        // Restart uses only its immutable selected bytes and the session/native owners. Disposing its
        // idle transport is safe and must not make session shutdown await the Restart that requested it.
        if (Volatile.Read(ref _active) == 1)
        {
            throw new InvalidOperationException("Observe cancellation before disposing update operations.");
        }
        _disposed = true;
        _transport.Dispose();
    }

    private async Task<UpdateStatus> RunAsync(bool download, IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return new UpdateStatus(UpdateStage.Failed, Failure: Failure.PolicyBlocked);
        }
        VerifiedUpdateSource? selected = _source;
        _source = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (download && selected is null)
            {
                return Publish(new UpdateStatus(UpdateStage.Failed, Failure: Failure.PolicyBlocked), progress);
            }
            Publish(new UpdateStatus(download ? UpdateStage.Downloading : UpdateStage.Checking, selected?.Manifest.Version), progress);
            // A cancellation cannot abandon the synchronous durable replay/append in the background.
            Outcome<UpdateTrustStore> opened = await Task.Run(OpenTrust).ConfigureAwait(false);
            using UpdateTrustStore? trust = opened.Value;
            cancellationToken.ThrowIfCancellationRequested();
            if (!opened.IsSuccess)
            {
                return Publish(new UpdateStatus(UpdateStage.Failed, Failure: opened.Failure), progress);
            }
            if (!download)
            {
                Outcome<VerifiedUpdateSource> discovered = await VerifiedUpdateSource.OpenAsync(
                    _transport,
                    trust!,
                    _installedVersion,
                    _profileFormat,
                    _migration,
                    Path.Combine(_stateDirectory, "packages"),
                    cancellationToken
                ).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!discovered.IsSuccess)
                {
                    return Publish(new UpdateStatus(UpdateStage.Failed, Failure: discovered.Failure), progress);
                }
                _source = discovered.Value;
                return Publish(new UpdateStatus(UpdateStage.Available, _source!.Manifest.Version), progress);
            }
            // The admitted snapshot must still be the durable head, including its exact signed digest.
            if (trust!.State.Pending is not null || trust.State.ManifestSha256 != selected!.ManifestSha256 ||
                !UpdateTrust.Compatible(selected.Manifest, _channel, _installedVersion, _profileFormat, _migration))
            {
                return Publish(new UpdateStatus(UpdateStage.Failed, Failure: Failure.PolicyBlocked), progress);
            }
            Outcome<string> staged = await selected.DownloadFullAsync(
                Path.Combine(_stateDirectory, "packages", selected.Manifest.Package.Name),
                () => Publish(new UpdateStatus(UpdateStage.Verifying, selected.Manifest.Version), progress),
                cancellationToken
            ).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (staged.IsSuccess)
            {
                _source = selected;
            }
            return Publish(staged.IsSuccess ? new UpdateStatus(UpdateStage.Ready, selected.Manifest.Version)
            {
                CanRestart = _restart is not null
            } : new UpdateStatus(UpdateStage.Failed, Failure: staged.Failure), progress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(new UpdateStatus(UpdateStage.Cancelled), progress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Publish(new UpdateStatus(UpdateStage.Failed, Failure: Failure.Unavailable), progress);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private UpdateStatus Publish(UpdateStatus status, IProgress<UpdateStatus> progress)
    {
        Volatile.Write(ref _current, status);
        progress.Report(status);
        return status;
    }

    private Outcome<UpdateTrustStore> OpenTrust()
    {
        string journal = Path.Combine(_stateDirectory, "trust.journal");
        // An absent journal beside retained cache/partial/other state is loss, not first installation.
        if (!File.Exists(journal) && Directory.Exists(_stateDirectory) && Directory.EnumerateFileSystemEntries(_stateDirectory).Any())
        {
            return Outcome<UpdateTrustStore>.Fail(Failure.NeedsReview);
        }
        return UpdateTrustStore.Open(journal, _root, _channel);
    }
}

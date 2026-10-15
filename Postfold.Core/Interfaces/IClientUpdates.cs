using Postfold.Core.Models.Updates;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>
/// Owns explicit updater operations. Reading Current must never start discovery or installation.
/// Progress reports are immutable; cancellation must finish before another operation is admitted.
/// </summary>
public interface IClientUpdates
{
    UpdateStatus Current { get; }

    Task<UpdateStatus> CheckAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken);

    Task<UpdateStatus> DownloadAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken);

    /// <summary>Requires an explicit user request and a separately qualified safe-shutdown/native verification boundary.</summary>
    Task<UpdateStatus> RestartAsync(CancellationToken cancellationToken);
}

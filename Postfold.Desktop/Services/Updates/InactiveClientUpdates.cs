using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Updates;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Updates;

/// <summary>No transport, timer, updater or shutdown dependency: this build cannot discover or apply updates.</summary>
internal sealed class InactiveClientUpdates : IClientUpdates
{
    internal InactiveClientUpdates(Failure failure = Failure.None)
    {
        Current = new UpdateStatus(failure == Failure.None ? UpdateStage.Inactive : UpdateStage.Failed, Failure: failure);
    }

    public UpdateStatus Current { get; }

    public Task<UpdateStatus> CheckAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public Task<UpdateStatus> DownloadAsync(IProgress<UpdateStatus> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public Task<UpdateStatus> RestartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }
}

using Postfold.Core.Models.Updates;
using System;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Optional application-owned updater activity shared by every Settings view.</summary>
public interface IClientUpdateActivity
{
    bool IsBusy { get; }
    bool IsCancelling { get; }

    /// <summary>Observers must marshal to their UI context and read Current, rather than retain stale events.</summary>
    event EventHandler? Changed;

    /// <summary>Applies successfully persisted preferences; a newly forbidden operation is cancelled and observed.</summary>
    void Configure(UpdatePreferences preferences);

    /// <summary>Requests cancellation and completes only after the active operation acknowledges it.</summary>
    Task CancelAsync();
}

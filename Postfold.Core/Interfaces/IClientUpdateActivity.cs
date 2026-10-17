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

    /// <summary>Applies successfully persisted preferences; newly forbidden discovery/download work is cancelled and observed.</summary>
    void Configure(UpdatePreferences preferences);

    /// <summary>Requests cancellation before shutdown commitment and observes the active operation. Committed restart is observed without promising cancellation.</summary>
    Task CancelAsync();
}

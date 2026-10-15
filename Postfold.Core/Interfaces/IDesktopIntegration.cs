using Postfold.Core.Enums;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Applies user-selected desktop preferences and exposes generic new-mail notifications.</summary>
public interface IDesktopIntegration
{
    bool TrayEnabled { get; }

    Task<DesktopPreferences> ReadPreferencesAsync(CancellationToken cancellationToken);

    Task<Failure> ConfigureAsync(DesktopPreferences preferences, CancellationToken cancellationToken);

    /// <summary>Changes update choices atomically without overwriting tray or notification preferences.</summary>
    Task<Failure> ConfigureUpdatesAsync(UpdatePreferences preferences, CancellationToken cancellationToken);

    Failure NotifyNewMail();
}

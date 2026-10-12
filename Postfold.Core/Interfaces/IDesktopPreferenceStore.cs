using Postfold.Core.Models.Appearance;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists desktop integration choices separately from applying operating-system behaviour.</summary>
public interface IDesktopPreferenceStore
{
    Task<DesktopPreferences> ReadDesktopPreferencesAsync(CancellationToken cancellationToken);

    Task SaveDesktopPreferencesAsync(DesktopPreferences preferences, CancellationToken cancellationToken);
}

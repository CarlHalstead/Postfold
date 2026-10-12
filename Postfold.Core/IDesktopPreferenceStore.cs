using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IDesktopPreferenceStore
{
    Task<DesktopPreferences> ReadDesktopPreferencesAsync(CancellationToken cancellationToken);
    Task SaveDesktopPreferencesAsync(DesktopPreferences preferences, CancellationToken cancellationToken);
}

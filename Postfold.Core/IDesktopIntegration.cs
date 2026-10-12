using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IDesktopIntegration
{
    bool TrayEnabled
    {
        get;
    }
    Task<DesktopPreferences> ReadPreferencesAsync(CancellationToken cancellationToken);
    Task<Failure> ConfigureAsync(DesktopPreferences preferences, CancellationToken cancellationToken);
    Failure NotifyNewMail();
}

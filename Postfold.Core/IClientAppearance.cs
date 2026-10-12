using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IClientAppearance
{
    Task<AppearanceSettings> ReadAsync(CancellationToken cancellationToken);
    Task<Failure> SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken);
}

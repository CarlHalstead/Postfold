using Postfold.Core.Enums;
using Postfold.Core.Models.Appearance;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Persists local appearance preferences without coupling storage to UI controls.</summary>
public interface IAppearanceStore
{
    Task<AppearanceSettings> ReadAsync(CancellationToken cancellationToken);

    Task<Failure> SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken);
}

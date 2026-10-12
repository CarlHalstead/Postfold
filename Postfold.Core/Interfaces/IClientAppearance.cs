using Postfold.Core.Enums;
using Postfold.Core.Models.Appearance;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Provides presentation with validated, locally persisted appearance settings.</summary>
public interface IClientAppearance
{
    Task<AppearanceSettings> ReadAsync(CancellationToken cancellationToken);

    Task<Failure> SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken);
}

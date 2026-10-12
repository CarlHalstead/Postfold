using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class ClientAppearance : IClientAppearance
{
    private readonly IAppearanceStore _store;

    public ClientAppearance(IAppearanceStore store)
    {
        _store = store;
    }

    public Task<AppearanceSettings> ReadAsync(CancellationToken cancellationToken)
    {
        return _store.ReadAsync(cancellationToken);
    }

    public Task<Failure> SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken)
    {
        return settings.IsValid ? _store.SaveAsync(settings, cancellationToken) : Task.FromResult(Failure.Validation);
    }
}

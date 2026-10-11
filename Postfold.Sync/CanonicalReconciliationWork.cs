using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class CanonicalReconciliationWork : IAccountWork
{
    private readonly ICanonicalMailboxReader _reader;
    private readonly ISynchronisationStore _store;
    private readonly int _limit;

    public CanonicalReconciliationWork(ICanonicalMailboxReader reader, ISynchronisationStore store, int limit)
    {
        _reader = reader;
        _store = store;
        _limit = limit;
    }

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        Checkpoint? checkpoint = await _store.ReadCheckpointAsync(_reader.Account, "$account", cancellationToken)
            .ConfigureAwait(false);
        Outcome<CanonicalPage> result = await _reader.ReadAsync(checkpoint, _limit, cancellationToken).ConfigureAwait(false);
        if (result.Failure == Failure.CursorExpired)
        {
            result = await _reader.ReadAsync(null, _limit, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsSuccess)
        {
            return result.Failure;
        }

        await _store.CommitAccountAsync(result.Value!, cancellationToken).ConfigureAwait(false);
        return Failure.None;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Sync;

public sealed class ReconciliationWork : IAccountWork
{
    private readonly IMailboxReader _reader;
    private readonly ISynchronisationStore _store;
    private readonly MailboxSnapshot _mailbox;
    private readonly int _batchSize;

    public ReconciliationWork(IMailboxReader reader, ISynchronisationStore store,
        MailboxSnapshot mailbox, int batchSize)
    {
        if (reader.Account != mailbox.Account)
        {
            throw new ArgumentException("Reader account does not match the mailbox.");
        }

        _reader = reader;
        _store = store;
        _mailbox = mailbox;
        _batchSize = batchSize;
    }

    public event EventHandler<Checkpoint>? Committed;

    public async Task<Failure> RunAsync(CancellationToken cancellationToken)
    {
        Checkpoint? checkpoint = await _store.ReadCheckpointAsync(_mailbox.Account, _mailbox.RemoteKey,
            cancellationToken).ConfigureAwait(false);
        Outcome<MetadataPage> result = await _reader.ReadAsync(_mailbox.RemoteKey, checkpoint, _batchSize,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Failure;
        }

        MetadataPage page = result.Value!;
        uint validity = page.Messages.Length > 0 ? page.Messages[0].Provider.Imap?.UidValidity ??
            _mailbox.UidValidity : _mailbox.UidValidity;
        if (page.Checkpoint.Generation.Contains(':') &&
            uint.TryParse(page.Checkpoint.Generation.Split(':')[0], out uint generationValidity))
        {
            validity = generationValidity;
        }

        await _store.CommitAsync(_mailbox with
        {
            UidValidity = validity
        }, page, cancellationToken).ConfigureAwait(false);
        Committed?.Invoke(this, page.Checkpoint);
        return Failure.None;
    }
}

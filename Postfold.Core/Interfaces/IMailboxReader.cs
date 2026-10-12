using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Synchronisation;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Discovers provider mailboxes and reads bounded metadata pages using account-scoped checkpoints.</summary>
public interface IMailboxReader : IAsyncDisposable
{
    AccountId Account
    {
        get;
    }
    Capabilities Capabilities
    {
        get;
    }

    Task<Outcome<ImmutableArray<MailboxSnapshot>>> DiscoverAsync(CancellationToken cancellationToken);

    Task<Outcome<MetadataPage>> ReadAsync(
        string scope,
        Checkpoint? checkpoint,
        int limit,
        CancellationToken cancellationToken
    );
}

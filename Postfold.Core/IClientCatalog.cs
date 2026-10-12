using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IClientCatalog
{
    Task<long> CachedMessageCountAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<CommandSnapshot>> CommandsAsync(AccountId account, CancellationToken cancellationToken);
    Task<ImmutableArray<AccountConfiguration>> AccountsAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<MailboxSnapshot>> MailboxesAsync(CancellationToken cancellationToken);
    Task<ImmutableArray<DraftSnapshot>> DraftsAsync(AccountId account, CancellationToken cancellationToken);
    Task<ImmutableArray<OutboxEntry>> OutboxAsync(AccountId account, CancellationToken cancellationToken);
    Task<ImmutableArray<DraftVersionSnapshot>> DraftVersionsAsync(AccountId account, Guid draft,
        CancellationToken cancellationToken);
}

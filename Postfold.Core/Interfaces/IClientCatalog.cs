using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads local account, mailbox and durable-work snapshots without initiating network access.</summary>
public interface IClientCatalog
{
    Task<long> CachedMessageCountAsync(CancellationToken cancellationToken);

    Task<ImmutableArray<CommandSnapshot>> CommandsAsync(AccountId account, CancellationToken cancellationToken);

    Task<ImmutableArray<AccountConfiguration>> AccountsAsync(CancellationToken cancellationToken);

    Task<ImmutableArray<MailboxSnapshot>> MailboxesAsync(CancellationToken cancellationToken);

    /// <summary>Returns at most twenty matching addresses from this account's local correspondents and durable submissions.</summary>
    Task<ImmutableArray<string>> RecognisedRecipientsAsync(AccountId account, string prefix, CancellationToken cancellationToken);

    /// <summary>Counts unread locally cached memberships in the current mailbox generation, never server totals.</summary>
    Task<ImmutableArray<MailboxUnreadCount>> MailboxUnreadCountsAsync(AccountId account, CancellationToken cancellationToken);

    Task<ImmutableArray<DraftSnapshot>> DraftsAsync(AccountId account, CancellationToken cancellationToken);

    Task<ImmutableArray<OutboxEntry>> OutboxAsync(AccountId account, CancellationToken cancellationToken);

    Task<ImmutableArray<DraftVersionSnapshot>> DraftVersionsAsync(
        AccountId account,
        Guid draft,
        CancellationToken cancellationToken
    );
}

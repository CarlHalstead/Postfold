using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Provides presentation with local mail queries and explicit account-scoped actions over durable engine work.</summary>
public interface IMailApplication
{
    /// <summary>Deletes the selected current draft locally and on its server, retaining offline intent.</summary>
    Task<Failure> DeleteDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);
    IClientCatalog Catalog { get; }

    /// <summary>Reports sync start, committed pages and completion on the worker thread; subscribers must dispatch UI work.</summary>
    event EventHandler<SynchronisationUpdate>? SynchronisationChanged;

    Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<Outcome<MimeSnapshot>> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);

    Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);

    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);

    Task<Failure> SaveAttachmentAsync(
        MessageSnapshot message,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    );

    Task<Failure> MutateAsync(
        MessageSnapshot message,
        MutationKind kind,
        bool value,
        string? destination,
        CancellationToken cancellationToken
    );

    Task<Failure> SynchroniseAsync(AccountId account, CancellationToken cancellationToken);

    /// <summary>Runs a scheduled mail check without activity notifications, retaining committed-change and failure reports.</summary>
    Task<Failure> SynchroniseScheduledAsync(AccountId account, CancellationToken cancellationToken);

    Task<Outcome<DraftSnapshot>> SaveDraftAsync(
        AccountId account,
        Guid id,
        ComposeContent content,
        CancellationToken cancellationToken
    );

    Task<Outcome<ComposeContent>> ReadDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);

    /// <summary>Resolves a folder message to its existing account-scoped editable draft, without creating a copy.</summary>
    Task<Outcome<DraftSnapshot>> FindDraftAsync(ProviderIdentity message, CancellationToken cancellationToken);

    /// <summary>Autosaves an existing editor against its last observed local revision, preserving concurrent changes as conflicts.</summary>
    Task<Outcome<DraftSnapshot>> SaveDraftRevisionAsync(DraftSnapshot expected, ComposeContent content, CancellationToken cancellationToken);

    Task<Failure> PushDraftAsync(AccountId account, Guid draft, CancellationToken cancellationToken);

    /// <summary>Closes the working draft into one version and optionally schedules automatic server synchronization.</summary>
    Task<Outcome<DraftSnapshot>> CloseDraftAsync(AccountId account, Guid draft, bool synchronise, CancellationToken cancellationToken);

    /// <summary>Observes an already-scheduled automatic draft save for UI refresh; it never starts another save.</summary>
    Task WaitForDraftSyncAsync(AccountId account, Guid draft, CancellationToken cancellationToken);

    /// <summary>Retains an exact local/remote pair for explicit draft review.</summary>
    Task<Outcome<DraftReviewSnapshot>> ReviewDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);

    /// <summary>Keeps the remote original and saves the reviewed local version as another remote draft.</summary>
    Task<Failure> CopyReviewedDraftAsync(DraftReviewSnapshot review, CancellationToken cancellationToken);

    Task<Outcome<ComposeAttachment>> ImportAttachmentAsync(
        AccountId account,
        string name,
        Stream source,
        CancellationToken cancellationToken
    );

    /// <summary>Freezes a submission durably. The compose Send action then calls SubmitAsync for this exact message.</summary>
    Task<Outcome<FrozenSubmission>> SendAsync(
        AccountId account,
        ComposeContent content,
        Guid draft,
        Guid? predecessor,
        CancellationToken cancellationToken
    );

    Task<Failure> ProcessOutboxAsync(AccountId account, CancellationToken cancellationToken);

    /// <summary>Attempts this account's exact queued message and its sent copy; never resubmits held or accepted mail.</summary>
    Task<Failure> SubmitAsync(AccountId account, Guid submission, CancellationToken cancellationToken);

    Task<Outcome<ComposeContent>> PrepareResendAsync(OutboxEntry entry, CancellationToken cancellationToken);

    Task<Failure> StoreSentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken);

    /// <summary>Retries only sent-copy storage after review; it must never resubmit the message.</summary>
    Task<Failure> RetrySentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken);

    Task<Failure> CancelQueuedAsync(OutboxEntry entry, CancellationToken cancellationToken);

    Task<Failure> ResolveCommandAsync(OperationEntry entry, bool retry, CancellationToken cancellationToken);
}

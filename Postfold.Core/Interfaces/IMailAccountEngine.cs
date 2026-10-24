using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Owns one account's synchronisation, durable mutations, drafts and outbox processing.</summary>
public interface IMailAccountEngine : IAsyncDisposable
{
    /// <summary>Persists a draft deletion and synchronises it when network policy permits.</summary>
    Task<Failure> DeleteDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);
    AccountId Account { get; }
    Capabilities Capabilities { get; }
    ImmutableArray<MailboxSnapshot> Mailboxes { get; }

    Task<Failure> ReconcileAsync(string mailbox, CancellationToken cancellationToken);

    Task<Failure> WaitForChangesAsync(string mailbox, TimeSpan maximumWait, CancellationToken cancellationToken);

    Task<Failure> QueueMutationAsync(MutationIntent intent, CancellationToken cancellationToken);

    Task<Failure> ProcessMutationsAsync(CancellationToken cancellationToken);

    Task<Outcome<DraftSnapshot>> SaveDraftAsync(Guid id, Stream content, CancellationToken cancellationToken);

    Task<Outcome<DraftSnapshot>> SaveDraftRevisionAsync(DraftSnapshot expected, Stream content, CancellationToken cancellationToken);

    Task<Failure> PushDraftAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finalizes locally before optionally scheduling an owned remote save; network failure never discards the closed version.</summary>
    Task<Outcome<DraftSnapshot>> CloseDraftAsync(Guid id, bool synchronise, CancellationToken cancellationToken);

    /// <summary>Observes this draft's already-owned automatic save without starting network work.</summary>
    Task WaitForDraftSyncAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Synchronises closed draft versions without finalizing open working drafts or processing the outbox.</summary>
    Task<Failure> SynchroniseDraftsAsync(CancellationToken cancellationToken);

    /// <summary>Observes both versions of a held draft without modifying remote mail.</summary>
    Task<Outcome<DraftReviewSnapshot>> ReviewDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);

    /// <summary>Creates another remote draft from the reviewed local revision, preserving the original remote draft.</summary>
    Task<Failure> CopyReviewedDraftAsync(DraftReviewSnapshot review, CancellationToken cancellationToken);

    Task<Outcome<FrozenSubmission>> QueueSubmissionAsync(
        ComposeContent content,
        Guid? sourceDraft,
        Guid? predecessor,
        CancellationToken cancellationToken
    );

    Task<Failure> ProcessOutboxAsync(CancellationToken cancellationToken);

    /// <summary>Immediately attempts the specified frozen message, then stores its sent copy after acceptance.</summary>
    Task<Failure> SubmitAsync(Guid submission, CancellationToken cancellationToken);

    Task<Failure> StoreSentCopyAsync(Guid submission, CancellationToken cancellationToken);

    Task<Failure> FetchBodyAsync(MessageSnapshot message, CancellationToken cancellationToken);

    Task<Failure> FetchFullMessageAsync(MessageSnapshot message, CancellationToken cancellationToken);

    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message, CancellationToken cancellationToken);

    Task<Failure> CopyAttachmentAsync(
        MessageSnapshot message,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    );
}

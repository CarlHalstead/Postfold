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
    AccountId Account
    {
        get;
    }
    Capabilities Capabilities
    {
        get;
    }
    ImmutableArray<MailboxSnapshot> Mailboxes
    {
        get;
    }

    Task<Failure> ReconcileAsync(string mailbox, CancellationToken cancellationToken);

    Task<Failure> WaitForChangesAsync(string mailbox, TimeSpan maximumWait, CancellationToken cancellationToken);

    Task<Failure> QueueMutationAsync(MutationIntent intent, CancellationToken cancellationToken);

    Task<Failure> ProcessMutationsAsync(CancellationToken cancellationToken);

    Task<Outcome<DraftSnapshot>> SaveDraftAsync(Guid id, Stream content, CancellationToken cancellationToken);

    Task<Failure> PushDraftAsync(Guid id, CancellationToken cancellationToken);

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

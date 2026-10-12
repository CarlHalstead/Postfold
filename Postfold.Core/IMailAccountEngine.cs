using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

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
    Task<Outcome<FrozenSubmission>> QueueSubmissionAsync(ComposeContent content, Guid? sourceDraft, Guid? predecessor,
        CancellationToken cancellationToken);
    Task<Failure> ProcessOutboxAsync(CancellationToken cancellationToken);
    Task<Failure> StoreSentCopyAsync(Guid submission, CancellationToken cancellationToken);
    Task<Failure> FetchBodyAsync(MessageSnapshot message, CancellationToken cancellationToken);
    Task<Failure> FetchFullMessageAsync(MessageSnapshot message, CancellationToken cancellationToken);
    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message,
        CancellationToken cancellationToken);
    Task<Failure> CopyAttachmentAsync(MessageSnapshot message, string part, Stream destination,
        CancellationToken cancellationToken);
}

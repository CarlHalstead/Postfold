using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMailApplication
{
    IClientCatalog Catalog
    {
        get;
    }
    Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<Outcome<MimeSnapshot>> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken);
    Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(MessageSnapshot message, bool retrieve,
        CancellationToken cancellationToken);
    Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message, bool retrieve,
        CancellationToken cancellationToken);
    Task<Failure> SaveAttachmentAsync(MessageSnapshot message, string part, Stream destination,
        CancellationToken cancellationToken);
    Task<Failure> MutateAsync(MessageSnapshot message, MutationKind kind, bool value, string? destination,
        CancellationToken cancellationToken);
    Task<Failure> SynchroniseAsync(AccountId account, CancellationToken cancellationToken);
    Task<Outcome<DraftSnapshot>> SaveDraftAsync(AccountId account, Guid id, ComposeContent content,
        CancellationToken cancellationToken);
    Task<Outcome<ComposeContent>> ReadDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken);
    Task<Failure> PushDraftAsync(AccountId account, Guid draft, CancellationToken cancellationToken);
    Task<Outcome<ComposeAttachment>> ImportAttachmentAsync(AccountId account, string name, Stream source,
        CancellationToken cancellationToken);
    Task<Outcome<FrozenSubmission>> SendAsync(AccountId account, ComposeContent content, Guid draft, Guid? predecessor,
        CancellationToken cancellationToken);
    Task<Failure> ProcessOutboxAsync(AccountId account, CancellationToken cancellationToken);
    Task<Outcome<ComposeContent>> PrepareResendAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<Failure> StoreSentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<Failure> RetrySentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<Failure> CancelQueuedAsync(OutboxEntry entry, CancellationToken cancellationToken);
    Task<Failure> ResolveCommandAsync(OperationEntry entry, bool retry, CancellationToken cancellationToken);
}

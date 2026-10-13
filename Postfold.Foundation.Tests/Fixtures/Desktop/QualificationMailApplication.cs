using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

/// <summary>Failure injection used only by the isolated, network-disabled qualification profile.</summary>
internal sealed class QualificationMailApplication(IMailApplication application) : IMailApplication
{
    public IClientCatalog Catalog => application.Catalog;
    internal bool FailDraftSaves
    {
        get; set;
    }

    public Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        return application.SearchAsync(request, cancellationToken);
    }

    public Task<Outcome<MimeSnapshot>> ReadAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken)
    {
        return application.ReadAsync(message, retrieve, cancellationToken);
    }

    public Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken)
    {
        return application.ReadHtmlAsync(message, retrieve, cancellationToken);
    }

    public Task<Outcome<ImmutableArray<AttachmentSnapshot>>> ReadAttachmentsAsync(MessageSnapshot message, bool retrieve, CancellationToken cancellationToken)
    {
        return application.ReadAttachmentsAsync(message, retrieve, cancellationToken);
    }

    public Task<Failure> SaveAttachmentAsync(MessageSnapshot message, string part, Stream destination, CancellationToken cancellationToken)
    {
        return application.SaveAttachmentAsync(message, part, destination, cancellationToken);
    }

    public Task<Failure> MutateAsync(
        MessageSnapshot message,
        MutationKind kind,
        bool value,
        string? destination,
        CancellationToken cancellationToken
    )
    {
        return application.MutateAsync(message, kind, value, destination, cancellationToken);
    }

    public Task<Failure> SynchroniseAsync(AccountId account, CancellationToken cancellationToken)
    {
        return application.SynchroniseAsync(account, cancellationToken);
    }

    public Task<Outcome<DraftSnapshot>> SaveDraftAsync(
        AccountId account,
        Guid id,
        ComposeContent content,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FailDraftSaves ? Task.FromResult(Outcome<DraftSnapshot>.Fail(Failure.Unavailable)) :
            application.SaveDraftAsync(account, id, content, cancellationToken);
    }

    public Task<Outcome<ComposeContent>> ReadDraftAsync(DraftSnapshot draft, CancellationToken cancellationToken)
    {
        return application.ReadDraftAsync(draft, cancellationToken);
    }

    public Task<Failure> PushDraftAsync(AccountId account, Guid draft, CancellationToken cancellationToken)
    {
        return application.PushDraftAsync(account, draft, cancellationToken);
    }

    public Task<Outcome<ComposeAttachment>> ImportAttachmentAsync(
        AccountId account,
        string name,
        Stream source,
        CancellationToken cancellationToken
    )
    {
        return application.ImportAttachmentAsync(account, name, source, cancellationToken);
    }

    public Task<Outcome<FrozenSubmission>> SendAsync(
        AccountId account,
        ComposeContent content,
        Guid draft,
        Guid? predecessor,
        CancellationToken cancellationToken
    )
    {
        return application.SendAsync(account, content, draft, predecessor, cancellationToken);
    }

    public Task<Failure> ProcessOutboxAsync(AccountId account, CancellationToken cancellationToken)
    {
        return application.ProcessOutboxAsync(account, cancellationToken);
    }

    public Task<Failure> SubmitAsync(AccountId account, Guid submission, CancellationToken cancellationToken)
    {
        return application.SubmitAsync(account, submission, cancellationToken);
    }

    public Task<Outcome<ComposeContent>> PrepareResendAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        return application.PrepareResendAsync(entry, cancellationToken);
    }

    public Task<Failure> StoreSentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        return application.StoreSentCopyAsync(entry, cancellationToken);
    }

    public Task<Failure> RetrySentCopyAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        return application.RetrySentCopyAsync(entry, cancellationToken);
    }

    public Task<Failure> CancelQueuedAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        return application.CancelQueuedAsync(entry, cancellationToken);
    }

    public Task<Failure> ResolveCommandAsync(OperationEntry entry, bool retry, CancellationToken cancellationToken)
    {
        return application.ResolveCommandAsync(entry, retry, cancellationToken);
    }
}

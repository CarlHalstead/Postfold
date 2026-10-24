using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Interfaces;

/// <summary>Saves draft MIME to a selected remote mailbox while retaining local blob and revision context.</summary>
internal interface IRemoteDraftTransport
{
    Task<Failure> DeleteDraftAsync(string mailbox, ProviderIdentity identity, CancellationToken cancellationToken);
    Task<RemoteDraftResult> SaveDraftAsync(
        string mailbox,
        IBlobStore blobs,
        DraftSnapshot draft,
        Stream content,
        CancellationToken cancellationToken
    );
}

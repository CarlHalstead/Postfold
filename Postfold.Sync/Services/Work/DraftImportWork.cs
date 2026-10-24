using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Sync.Services.Work;

internal sealed class DraftImportWork(
    AccountId account,
    IRemoteDraftSource source,
    IDraftStore store,
    IBlobStore blobs,
    IMimeDraftReader reader,
    long maximumBytes,
    string? cursor
) : IAccountWork
{
    public string? Next { get; private set; }

    public async Task<Failure> RunAsync(
        CancellationToken cancellationToken
    )
    {
        Outcome<RemoteDraftPage> page = await source.ReadDraftPageAsync(cursor, 50, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return page.Failure;
        }
        foreach (RemoteDraftResult remote in page.Value!.Drafts)
        {
            if (remote.Identity?.Account != account || remote.ConflictingContent?.Account != account || remote.Version is null)
            {
                return Failure.Validation;
            }
            await using Stream mime = await blobs.OpenAsync(remote.ConflictingContent, cancellationToken).ConfigureAwait(false);
            Outcome<ComposeContent> parsed = await reader.ReadComposeAsync(account, mime, cancellationToken).ConfigureAwait(false);
            if (!parsed.IsSuccess)
            {
                return parsed.Failure;
            }
            ComposeContent content = parsed.Value! with
            {
                Recipients = parsed.Value!.Recipients.IsDefault ? [] : parsed.Value.Recipients,
                Attachments = parsed.Value.Attachments.IsDefault ? [] : parsed.Value.Attachments
            };
            using MemoryStream json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(content));
            BlobReference local = await blobs.WriteAsync(account, json, maximumBytes, true, cancellationToken).ConfigureAwait(false);
            await store.ImportRemoteAsync(remote, local, cancellationToken).ConfigureAwait(false);
        }
        Next = page.Value.Next;
        return Failure.None;
    }
}

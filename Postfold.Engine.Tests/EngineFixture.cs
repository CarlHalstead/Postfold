using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;
using Postfold.Storage;

namespace Postfold.Engine.Tests;

internal static class EngineFixture
{
    internal static readonly AccountId Account = new AccountId(1);

    internal static string DirectoryPath()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Postfold.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException();
        }

        string path = Path.Combine(root, "artifacts", "engine-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static async Task<SqliteProfile> OpenAsync(string directory)
    {
        Outcome<SqliteProfile> outcome = await SqliteProfile.OpenAsync(Path.Combine(directory, "profile.db"),
            TimeProvider.System, CancellationToken.None);
        SqliteProfile profile = outcome.Value ?? throw new InvalidDataException(outcome.Failure.ToString());
        await profile.AddAccountAsync(Account, "Synthetic one", CancellationToken.None);
        await profile.AddAccountAsync(new AccountId(2), "Synthetic two", CancellationToken.None);
        return profile;
    }

    internal static MailboxSnapshot Mailbox(uint validity = 7, AccountId? account = null)
    {
        return new MailboxSnapshot(account ?? Account, new FolderId(0), "INBOX", "Inbox",
            MailboxRole.Inbox, true, true, validity);
    }

    internal static MessageSnapshot Message(uint uid, uint validity = 7, AccountId? account = null)
    {
        AccountId owner = account ?? Account;
        ImapIdentity imap = new ImapIdentity(owner, "INBOX", validity, uid);
        return new MessageSnapshot(owner, new MessageId(0), new ProviderIdentity(owner,
            "INBOX:" + validity + ":" + uid, imap), "Café résumé " + uid, "Synthetic Sender",
            "same-id@synthetic.invalid", DateTimeOffset.UnixEpoch.AddMinutes(uid), 100, false, false,
            ContentAvailability.Missing, PendingState.None);
    }

    internal static MetadataPage Page(ImmutableArray<MessageSnapshot> messages, string generation = "7:g",
        bool complete = true, AccountId? account = null)
    {
        return new MetadataPage(new Checkpoint(account ?? Account, "INBOX", generation,
            complete ? null : "1", 0, complete), messages, [], true);
    }

    internal static async Task<MessageSnapshot> FirstAsync(SqliteProfile profile, AccountId? account = null)
    {
        Outcome<SearchPage> result = await profile.SearchAsync(new SearchRequest(account ?? Account, false,
            "", null, false, 10), CancellationToken.None);
        return result.Value!.Messages[0];
    }

    internal static async Task<BlobReference> BlobAsync(SqliteProfile profile, string content, bool protect = true)
    {
        using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await profile.Blobs.WriteAsync(Account, source, 1048576, protect, CancellationToken.None);
    }

    internal static FrozenSubmission Submission(BlobReference blob)
    {
        return new FrozenSubmission(Guid.NewGuid(), Account, blob, "sender@synthetic.invalid",
            ["first@synthetic.invalid", "second@synthetic.invalid"]);
    }
}

using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

internal sealed class SyntheticProfileSeeder
{
    private readonly SqliteProfile _profile;
    private readonly IMimePipeline _mime;
    private readonly IWorkPolicy _policy;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly EngineLimits _limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);

    internal SyntheticProfileSeeder(DesktopMailRuntime host)
    {
        _profile = (SqliteProfile)host.Catalog;
        _policy = new EnginePolicy(host.NetworkAllowed, false, false);
        _mime = new MimePipeline(_limits.MaximumMimeBytes, _limits.MaximumMimeParts, _profile.Blobs);
    }

    public async Task SeedSyntheticAsync(int count, CancellationToken cancellationToken)
    {
        if (_policy.AllowNetwork || count is < 3 or > 100000)
        {
            throw new InvalidOperationException("Synthetic profiles require network-disabled composition.");
        }

        if (!(await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false)).IsEmpty)
        {
            await EnsureSyntheticFoldersAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset epoch = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        for (int accountIndex = 1; accountIndex <= 3; accountIndex++)
        {
            AccountId account = new AccountId(accountIndex);
            await _profile.AddAccountAsync(account, "Synthetic " + accountIndex, cancellationToken).ConfigureAwait(false);
            MailEndpoint endpoint = new MailEndpoint(
                "synthetic.invalid",
                993,
                TlsMode.Implicit,
                "sender" + accountIndex + "@example.invalid",
                "synthetic"
            );
            await _profile.ConfigureAccountAsync(
                account,
                endpoint,
                endpoint with
                {
                    Port = 465
                },
                cancellationToken
            ).ConfigureAwait(false);
            int accountCount = count / 3 + (accountIndex <= count % 3 ? 1 : 0);
            for (int offset = 0; offset < accountCount; offset += 500)
            {
                ImmutableArray<MessageSnapshot>.Builder messages = ImmutableArray.CreateBuilder<MessageSnapshot>();
                for (int index = offset; index < Math.Min(accountCount, offset + 500); index++)
                {
                    ImapIdentity identity = new ImapIdentity(account, "INBOX", 1, checked((uint)index + 1));
                    messages.Add(new MessageSnapshot(
                        account,
                        new MessageId(index + 1),
                        new ProviderIdentity(account, identity.Uid.ToString(), identity),
                        "Synthetic résumé message " + index,
                        "fixture@example.invalid",
                        null,
                        epoch.AddMinutes(-index),
                        256,
                        index % 3 == 0,
                        index % 5 == 0,
                        ContentAvailability.Missing,
                        PendingState.None
                    ));
                }

                MailboxSnapshot mailbox = new MailboxSnapshot(
                    account,
                    new FolderId(accountIndex),
                    "INBOX",
                    "Inbox",
                    MailboxRole.Inbox,
                    true,
                    true,
                    1
                );
                Checkpoint checkpoint = new Checkpoint(
                    account,
                    "INBOX",
                    "1:synthetic",
                    null,
                    0,
                    offset + 500 >= accountCount
                );
                await _profile.CommitAsync(
                    mailbox,
                    new MetadataPage(checkpoint, messages.ToImmutable(), [], true),
                    cancellationToken
                ).ConfigureAwait(false);
            }

            Outcome<SearchPage> page = await _profile.SearchAsync(
                new SearchRequest(account, false, "", null, false, 20),
                cancellationToken
            ).ConfigureAwait(false);
            foreach (MessageSnapshot message in page.Value!.Messages)
            {
                string mime = "From: fixture@example.invalid\r\nTo: sender@example.invalid\r\nSubject: " + message.Subject +
                    "\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
                    "This is synthetic cached mail. No network or real credentials are used.\n" +
                    "Try search, selection, read/flag commands and durable local draft autosave.";
                using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(mime));
                BlobReference blob = await _profile.Blobs.WriteAsync(
                    account,
                    source,
                    _limits.MaximumMimeBytes,
                    false,
                    cancellationToken
                ).ConfigureAwait(false);
                source.Position = 0;
                Outcome<MimeSnapshot> parsed = await _mime.ParseAsync(source, cancellationToken).ConfigureAwait(false);
                await _profile.StoreBodyAsync(account, message.Id, blob, parsed.Value!, cancellationToken).ConfigureAwait(false);
            }
        }

        await EnsureSyntheticFoldersAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SeedDesignFixtureAsync(CancellationToken cancellationToken)
    {
        if (_policy.AllowNetwork)
        {
            throw new InvalidOperationException("Design fixtures require network-disabled composition.");
        }
        AccountId account = new AccountId(1);
        SearchPage page = (await _profile.SearchAsync(new SearchRequest(account, false, "", null, false, 8), cancellationToken)
            .ConfigureAwait(false)).Value!;
        string[] subjects = ["Project update", "Security information updated", "Lunch next week?", "Team notes", "Your weekly mix", "Holiday plans", "Booking confirmed", "Development update"];
        string[] senders = ["Morgan Lee", "Account security", "Alex Carter", "Team workspace", "Music digest", "Emily Rivers", "Travel desk", "Project notices"];
        DateTimeOffset now = _time.GetUtcNow();
        ImmutableArray<MessageSnapshot> messages = page.Messages.Select((message, index) => message with
        {
            Subject = subjects[index],
            Participants = senders[index] + " <sender" + index + "@synthetic.invalid>",
            Date = now.AddDays(index < 3 ? 0 : index < 6 ? -1 : -2).AddMinutes(-index * 30),
            Read = index is not 0 and not 1,
            Flagged = false
        }).ToImmutableArray();
        MailboxSnapshot inbox = (await _profile.MailboxesAsync(cancellationToken).ConfigureAwait(false))
            .Single(mailbox => mailbox.Account == account && mailbox.Role == MailboxRole.Inbox);
        await _profile.CommitAsync(inbox, new MetadataPage(
            new Checkpoint(account, inbox.RemoteKey, "1:synthetic", null, 0, false),
            messages,
            [],
            false
        ), cancellationToken).ConfigureAwait(false);
        foreach (MessageSnapshot message in messages)
        {
            string body = "Hi,\n\nHere is the latest project update. We are making good progress and are on track for the next milestone.\n\n" +
                "I have attached the updated plan and a few notes from the meeting. Let me know if you have any questions.\n\nThanks,\nMorgan";
            string mime = "From: " + message.Participants + "\r\nTo: sender1@example.invalid\r\nCc: colleague@synthetic.invalid\r\n" +
                "Subject: " + message.Subject + "\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=pf049\r\n\r\n" +
                "--pf049\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + body + "\r\n" +
                "--pf049\r\nContent-Type: text/plain; name=Project-plan.txt\r\nContent-Disposition: attachment; filename=Project-plan.txt\r\n\r\nSynthetic project plan\r\n" +
                "--pf049\r\nContent-Type: text/csv; name=Meeting-notes.csv\r\nContent-Disposition: attachment; filename=Meeting-notes.csv\r\n\r\nItem,Status\r\nLayout,Review\r\n--pf049--\r\n";
            if (message.Subject == "Lunch next week?")
            {
                mime = "From: " + message.Participants + "\r\nTo: colleague@synthetic.invalid\r\n" +
                    "Subject: " + message.Subject + "\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
                    "Hi,\n\nWould you like to meet for lunch next week?\n\nThanks,\nAlex";
            }
            else if (message.Subject == "Development update")
            {
                string longBody = string.Join("\n\n", Enumerable.Range(1, 20).Select(index =>
                    "Synthetic review paragraph " + index + ": the reader keeps long received text selectable and scrolls attachments after the complete body."));
                string attachments = string.Concat(Enumerable.Range(1, 8).Select(index =>
                    "--pf049\r\nContent-Type: text/plain\r\nContent-Disposition: attachment; filename=Review-notes-" + index +
                    ".txt\r\n\r\nAuthored synthetic review notes " + index + "\r\n"));
                mime = "From: " + message.Participants + "\r\nTo: sender1@example.invalid\r\n" +
                    "Subject: " + message.Subject + "\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=pf049\r\n\r\n" +
                    "--pf049\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + longBody + "\r\n" + attachments + "--pf049--\r\n";
            }
            using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(mime));
            BlobReference blob = await _profile.Blobs.WriteAsync(account, source, _limits.MaximumMimeBytes, false, cancellationToken)
                .ConfigureAwait(false);
            source.Position = 0;
            MimeSnapshot content = (await _mime.ParseAsync(source, cancellationToken).ConfigureAwait(false)).Value!;
            await _profile.StoreBodyAsync(account, message.Id, blob, content, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task SeedReadingFixtureAsync(CancellationToken cancellationToken)
    {
        if (_policy.AllowNetwork)
        {
            throw new InvalidOperationException("Qualification fixtures require network-disabled composition.");
        }

        AccountId account = new AccountId(1);
        ImmutableArray<MessageSnapshot> messages = (await _profile.SearchAsync(
            new SearchRequest(account, false, "", null, false, 20),
            cancellationToken
        ).ConfigureAwait(false)).Value!.Messages;
        string mime = "From: fixture@example.invalid\r\nTo: sender@example.invalid\r\n" +
            "Subject: Authored reading qualification\r\nMIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=outer\r\n\r\n--outer\r\n" +
            "Content-Type: multipart/related; boundary=related\r\n\r\n--related\r\n" +
            "Content-Type: multipart/alternative; boundary=alternative\r\n\r\n--alternative\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\nAuthored synthetic received attachment and HTML fixture.\r\n" +
            "--alternative\r\nContent-Type: text/html; charset=utf-8\r\n\r\n" +
            "<p>Authored synthetic HTML fixture</p><img src='cid:pixel' alt='Selected-message pixel'>" +
            "<img src='https://images.synthetic.invalid/pixel' alt='Blocked remote image'>" +
            "<script>globalThis.attack=true</script>\r\n--alternative--\r\n" +
            "--related\r\nContent-Type: image/png\r\nContent-ID: <pixel>\r\n" +
            "Content-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(QualificationImage.CreatePixel()) + "\r\n" +
            "--related--\r\n--outer\r\nContent-Type: application/octet-stream\r\n" +
            "Content-Disposition: attachment; filename=\"../../report.txt\"\r\n\r\n" +
            "Authored synthetic attachment.\r\n--outer--\r\n";
        using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(mime));
        BlobReference blob = await _profile.Blobs.WriteAsync(
            account,
            source,
            _limits.MaximumMimeBytes,
            false,
            cancellationToken
        ).ConfigureAwait(false);
        source.Position = 0;
        Outcome<MimeSnapshot> parsed = await _mime.ParseAsync(source, cancellationToken).ConfigureAwait(false);
        if (!parsed.IsSuccess)
        {
            throw new InvalidOperationException("Authored qualification MIME is invalid.");
        }

        foreach (MessageSnapshot message in messages)
        {
            await _profile.StoreBodyAsync(account, message.Id, blob, parsed.Value!, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureSyntheticFoldersAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<MailboxSnapshot> all = await _profile.MailboxesAsync(cancellationToken).ConfigureAwait(false);
        foreach (AccountConfiguration account in await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (account.Incoming.Host != "synthetic.invalid")
            {
                continue;
            }

            ImmutableArray<MailboxSnapshot>.Builder folders = all.Where(item => item.Account == account.Id)
                .ToImmutableArray().ToBuilder();
            foreach (MailboxRole role in new[] { MailboxRole.Archive, MailboxRole.Trash, MailboxRole.Sent, MailboxRole.Drafts })
            {
                if (!folders.Any(item => item.Role == role))
                {
                    folders.Add(new MailboxSnapshot(
                        account.Id,
                        new FolderId(0),
                        role.ToString(),
                        role.ToString(),
                        role,
                        true,
                        true,
                        1
                    ));
                }
            }

            await _profile.SaveMailboxesAsync(account.Id, folders.ToImmutable(), null, cancellationToken).ConfigureAwait(false);
            await _profile.SaveCapabilitiesAsync(
                account.Id,
                new Capabilities(false, false, false, true, true, true),
                cancellationToken
            ).ConfigureAwait(false);
        }
    }

}

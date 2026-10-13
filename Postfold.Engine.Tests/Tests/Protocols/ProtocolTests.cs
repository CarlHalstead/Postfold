using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Protocols;

public sealed class ProtocolTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadAsync_WhenInboxHasSparseUidsOrNoUidNext_LoadsItsMessagesInBoundedPages(bool omitUidNext)
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(true)
        {
            CurrentUids = [3, 4000000],
            UidNext = omitUidNext ? null : 4000001
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        MailboxSnapshot inbox = (await adapter.DiscoverAsync(CancellationToken.None)).Value.Single(item => item.Role == MailboxRole.Inbox);
        ReconciliationWork work = new ReconciliationWork(adapter, profile, inbox, 1);

        // Act
        Failure first = await work.RunAsync(CancellationToken.None);
        Checkpoint? continuation = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);
        Failure second = await work.RunAsync(CancellationToken.None);
        MailboxSnapshot persisted = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.Role == MailboxRole.Inbox);
        SearchPage page = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", persisted.Id, false, 10),
            CancellationToken.None)).Value!;

        // Assert
        Assert.That(first, Is.EqualTo(Failure.None));
        Assert.That(second, Is.EqualTo(Failure.None));
        Assert.That(continuation!.Complete, Is.False);
        Assert.That(continuation.Cursor, Is.EqualTo("3999999"));
        Assert.That(page.Messages.Select(message => message.Provider.Imap!.Uid), Is.EquivalentTo(new uint[] { 3, 4000000 }));
        Assert.That(server.Commands.Count(command => command.Contains("UID FETCH", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That((await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None))!.Complete, Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyAsync_WhenSafeMoveCapabilityExists_RemovesOnlySelectedSource(bool fallback)
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            AdvancedImap = true,
            OmitNativeMove = fallback,
            IncludeArchive = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await adapter.DiscoverAsync(CancellationToken.None);
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "INBOX:7:1",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );

        // Act
        Failure result = await adapter.ApplyAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            EngineFixture.Account,
            new MessageId(1),
            identity,
            MutationKind.Move,
            false,
            "Archive"
        ), CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(server.CurrentUids, Is.EqualTo(new uint[] { 2 }));
        Assert.That(server.ArchiveUids, Is.EqualTo(new uint[] { 3 }));
        Assert.That(server.Commands.Any(command => command.Contains(
            fallback ? "UID COPY" : "UID MOVE",
            StringComparison.Ordinal
        )), Is.True);
        Assert.That(server.Commands.Any(command => command.EndsWith(" EXPUNGE", StringComparison.Ordinal)), Is.False);
        Assert.That(
            server.Commands.Any(command => command.Contains("UID EXPUNGE", StringComparison.Ordinal)),
            Is.EqualTo(fallback)
        );
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DiscoverAsync_WhenStartTlsIsRequired_NeverAuthenticatesOnPlainTransport(bool advertised)
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true, startTls: true)
        {
            AdvertiseStartTls = advertised
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.RequiredStartTls, "synthetic", "fixture"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };

        // Act
        Outcome<ImmutableArray<MailboxSnapshot>> result = await reader.DiscoverAsync(CancellationToken.None);
        int authentication = server.Commands.FindIndex(command => command.Contains("AUTHENTICATE", StringComparison.Ordinal));
        int tls = server.Commands.FindIndex(command => command.EndsWith(" STARTTLS", StringComparison.Ordinal));

        // Assert
        Assert.That(result.IsSuccess, Is.EqualTo(advertised));
        if (advertised)
        {
            Assert.That(authentication, Is.GreaterThan(tls));
            Assert.That(tls, Is.GreaterThanOrEqualTo(0));
        }
        else
        {
            Assert.That(authentication, Is.EqualTo(-1));
        }
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task SubmitAsync_WhenServerConcludesNonAcceptance_ReturnsKnownRejection(
        bool rejectRecipients,
        bool rejectData
    )
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: false)
        {
            RejectAllRecipients = rejectRecipients,
            RejectData = rejectData
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        SmtpSubmitter submitter = new SmtpSubmitter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials
        )
        {
            FixtureAuthority = server.Authority
        };
        using MemoryStream mime = new MemoryStream();
        await new MimePipeline(1048576, 100).FreezeAsync(new ComposeContent(
            "sender@synthetic.invalid",
            ["first@synthetic.invalid", "second@synthetic.invalid"],
            "Synthetic",
            "body",
            DateTimeOffset.UnixEpoch,
            "id@synthetic.invalid"
        ), mime, CancellationToken.None);
        FrozenSubmission frozen = EngineFixture.Submission(new BlobReference(
            Guid.NewGuid(),
            EngineFixture.Account,
            mime.Length,
            Convert.ToHexString(SHA256.HashData(mime.ToArray())),
            true
        ));
        mime.Position = 0;

        // Act
        SubmissionOutcome result = await submitter.SubmitAsync(frozen, mime, CancellationToken.None);

        // Assert
        Assert.That(result.State, Is.EqualTo(SubmissionState.Rejected));
        Assert.That(result.Recipients.All(recipient => recipient.State != RecipientState.Accepted), Is.True);
        Assert.That(server.SubmissionCount, Is.EqualTo(rejectRecipients ? 0 : 1));
    }

    [TestCase(8u, true)]
    [TestCase(7u, false)]
    public async Task ApplyAsync_WhenGenerationOrTargetIsStale_RefusesRemoteMutation(uint validity, bool present)
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            UidValidity = validity,
            CurrentUids = present ? [1] : [2]
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await adapter.DiscoverAsync(CancellationToken.None);
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "INBOX:7:1",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );

        // Act
        Failure result = await adapter.ApplyAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            EngineFixture.Account,
            new MessageId(1),
            identity,
            MutationKind.SetRead,
            true,
            null
        ), CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(server.Commands.Any(command => command.Contains("STORE", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task OpenBodyAsync_WhenMetadataDescribesText_RetrievesOnlySelectedBodyPart()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await reader.DiscoverAsync(CancellationToken.None);
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "key",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );

        // Act
        Outcome<Stream> content = await reader.OpenBodyAsync(identity, CancellationToken.None);
        await using Stream owned = content.Value!;
        Outcome<MimeSnapshot> parsed = await new MimePipeline(1048576, 100).ParseAsync(owned, CancellationToken.None);

        // Assert
        Assert.That(content.IsSuccess, Is.True);
        Assert.That(parsed.Value!.PlainText, Does.Contain("synthetic body"));
        Assert.That(server.Commands.Any(command => command.Contains("BODY.PEEK[]", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task PushDraftAsync_WhenAppendAcknowledgementIsLost_HoldsAndDoesNotAppendTwice()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            AdvancedImap = true,
            LoseAcknowledgement = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await adapter.DiscoverAsync(CancellationToken.None);
        IRemoteDraftStore remote = adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!;
        DraftSnapshot draft = await profile.SaveAsync(
            Guid.NewGuid(),
            EngineFixture.Account,
            await EngineFixture.BlobAsync(
    profile,
    "From: sender@synthetic.invalid\r\nSubject: draft\r\n\r\nlocal body\r\n"
),
            CancellationToken.None
        );
        DraftWork work = new DraftWork(draft.Id, draft.Account, profile, profile.Blobs, remote);

        // Act
        Failure first = await work.RunAsync(CancellationToken.None);
        Failure second = await work.RunAsync(CancellationToken.None);
        DraftSnapshot? saved = await profile.ReadAsync(draft.Id, draft.Account, CancellationToken.None);

        // Assert
        Assert.That(first, Is.EqualTo(Failure.NeedsReview));
        Assert.That(second, Is.EqualTo(Failure.NeedsReview));
        Assert.That(saved!.Held, Is.True);
        Assert.That(server.Commands.Count(command => command.Contains("APPEND", StringComparison.Ordinal)), Is.EqualTo(1));
    }

    [Test]
    public async Task ReadAsync_WhenCondStoreAndQuickResyncAreAvailable_ReconcilesMissedDeletion()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            AdvancedImap = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        Outcome<ImmutableArray<MailboxSnapshot>> discovered = await reader.DiscoverAsync(CancellationToken.None);
        ReconciliationWork work = new ReconciliationWork(reader, profile, discovered.Value[0], 100);
        await work.RunAsync(CancellationToken.None);
        server.CurrentUids = [1];

        // Act
        Failure result = await work.RunAsync(CancellationToken.None);
        Outcome<SearchPage> search = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(search.Value!.Messages, Has.Length.EqualTo(1));
        Assert.That(search.Value.Messages[0].Provider.Imap!.Uid, Is.EqualTo(1));
        Assert.That(server.Commands.Any(command => command.Contains("QRESYNC", StringComparison.Ordinal)), Is.True);
        Assert.That(server.Commands.Any(command => command.Contains("CHANGEDSINCE", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task WaitAsync_WhenIdleSignalsExistenceChange_StillRequiresAuthoritativeReconciliation()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            AdvancedImap = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await reader.DiscoverAsync(CancellationToken.None);
        IMailboxSignal signal = reader.CreateIdleSignal()!;

        // Act
        Failure result = await signal.WaitAsync("INBOX", TimeSpan.FromMinutes(1), CancellationToken.None);
        Outcome<MetadataPage> authoritative = await reader.ReadAsync("INBOX", null, 100, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(authoritative.Value!.Messages, Has.Length.EqualTo(2));
        Assert.That(server.Commands, Does.Contain("DONE"));
    }

    [Test]
    public async Task PushDraftAsync_WhenRemoteContentChanges_PreservesConflictWithoutAppending()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true)
        {
            AdvancedImap = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await adapter.DiscoverAsync(CancellationToken.None);
        IRemoteDraftStore remote = adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!;
        BlobReference local = await EngineFixture.BlobAsync(
            profile,
            "From: sender@synthetic.invalid\r\nSubject: draft\r\n\r\nlocal body\r\n"
        );
        DraftSnapshot draft = await profile.SaveAsync(Guid.NewGuid(), EngineFixture.Account, local, CancellationToken.None);
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "remote",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );
        await profile.AcknowledgeAsync(
            draft,
            new RemoteDraftResult(identity, "prior-version", null, false, Failure.None),
            CancellationToken.None
        );
        await profile.SaveAsync(draft.Id, draft.Account, local, CancellationToken.None);

        // Act
        Failure result = await new DraftWork(draft.Id, draft.Account, profile, profile.Blobs, remote)
            .RunAsync(CancellationToken.None);
        DraftSnapshot? preserved = await profile.ReadAsync(draft.Id, draft.Account, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(preserved!.Conflict, Is.True);
        Assert.That(preserved.Content, Is.EqualTo(local));
        Assert.That(server.Commands.Any(command => command.Contains("APPEND", StringComparison.Ordinal)), Is.False);
    }

    [TestCase(false, false, SubmissionState.Accepted)]
    [TestCase(true, false, SubmissionState.PartiallyAccepted)]
    [TestCase(false, true, SubmissionState.Uncertain)]
    public async Task SubmitAsync_WhenLocalServerReturnsEvidence_PreservesRecipientSubsets(
        bool partial,
        bool lostAcknowledgement,
        SubmissionState expected
    )
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: false)
        {
            RejectSecondRecipient = partial,
            LoseAcknowledgement = lostAcknowledgement
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic-password", false), CancellationToken.None);
        SmtpSubmitter submitter = new SmtpSubmitter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials
        )
        {
            FixtureAuthority = server.Authority
        };
        MimePipeline pipeline = new MimePipeline(1048576, 100);
        using MemoryStream mime = new MemoryStream();
        await pipeline.FreezeAsync(new ComposeContent(
            "sender@synthetic.invalid",
            ["first@synthetic.invalid", "second@synthetic.invalid"],
            "Synthetic subject",
            "Synthetic body",
            DateTimeOffset.UnixEpoch,
            "frozen@synthetic.invalid"
        ), mime, CancellationToken.None);
        string frozenBytes = Encoding.UTF8.GetString(mime.ToArray());
        mime.Position = 0;
        FrozenSubmission frozen = EngineFixture.Submission(new BlobReference(
            Guid.NewGuid(),
            EngineFixture.Account,
            mime.Length,
            Convert.ToHexString(SHA256.HashData(mime.ToArray())),
            true
        ));

        // Act
        SubmissionOutcome result = await submitter.SubmitAsync(frozen, mime, CancellationToken.None);

        // Assert
        Assert.That(result.State, Is.EqualTo(expected));
        Assert.That(server.SubmissionCount, Is.EqualTo(1));
        Assert.That(server.SubmittedMime, Is.EqualTo(frozenBytes));
        Assert.That(
            result.Recipients[0].State,
            Is.EqualTo(lostAcknowledgement ? RecipientState.Uncertain : RecipientState.Accepted)
        );
        Assert.That(
            result.Recipients[1].State,
            Is.EqualTo(lostAcknowledgement ? RecipientState.Uncertain :
                partial ? RecipientState.Rejected : RecipientState.Accepted)
        );
    }

    [Test]
    public async Task SubmitAsync_WhenStartTlsIsRequired_AuthenticatesOnlyAfterTls()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: false, startTls: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic-password", false), CancellationToken.None);
        SmtpSubmitter submitter = new SmtpSubmitter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.RequiredStartTls, "synthetic", "synthetic"),
            credentials
        )
        {
            FixtureAuthority = server.Authority
        };
        using MemoryStream mime = new MemoryStream(Encoding.ASCII.GetBytes(
            "From: sender@synthetic.invalid\r\nTo: first@synthetic.invalid\r\nSubject: synthetic\r\n\r\nbody\r\n"
        ));

        // Act
        SubmissionOutcome result = await submitter.SubmitAsync(
            EngineFixture.Submission(new BlobReference(
    Guid.NewGuid(),
    EngineFixture.Account,
    mime.Length,
    Convert.ToHexString(SHA256.HashData(mime.ToArray())),
    true
)),
            mime,
            CancellationToken.None
        );

        // Assert
        Assert.That(result.State, Is.EqualTo(SubmissionState.Accepted));
        Assert.That(
            server.Commands.FindIndex(line => line.StartsWith("AUTH ", StringComparison.Ordinal)),
            Is.GreaterThan(server.Commands.IndexOf("STARTTLS"))
        );
    }

    [Test]
    public async Task DiscoverAsync_WhenRealImapFixtureResponds_UsesExactGenerationsAndCommitsMetadata()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic-password", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());

        // Act
        Outcome<ImmutableArray<MailboxSnapshot>> discovery = await reader.DiscoverAsync(CancellationToken.None);
        MailboxSnapshot mailbox = discovery.Value.Single(item => item.Role == MailboxRole.Inbox);
        ReconciliationWork work = new ReconciliationWork(reader, profile, mailbox, 50);
        Failure result = await work.RunAsync(CancellationToken.None);
        Outcome<SearchPage> search = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "synthetic",
            null,
            false,
            10
        ), CancellationToken.None);
        Checkpoint? checkpoint = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(search.Value!.Messages, Has.Length.EqualTo(2));
        Assert.That(search.Value.Messages.All(item => item.Provider.Imap!.UidValidity == 7), Is.True);
        Assert.That(checkpoint!.Complete, Is.True);
        Assert.That(reader.Capabilities.NativeMove, Is.False);
    }

    [Test]
    public async Task DiscoverAsync_WhenCertificateIsUntrusted_RejectsBeforeAuthentication()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic-password", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        );

        // Act
        Outcome<ImmutableArray<MailboxSnapshot>> result = await reader.DiscoverAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.TlsRejected));
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(server.Commands.Any(line => line.Contains("AUTH", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task ApplyAsync_WhenMoveLacksSelectiveDeletion_RetainsSourceAndRequiresReview()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(imap: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic-password", false), CancellationToken.None);
        await using ImapAdapter adapter = new ImapAdapter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials,
            1048576
        )
        {
            FixtureAuthority = server.Authority
        };
        await adapter.DiscoverAsync(CancellationToken.None);
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "key",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );

        // Act
        Failure result = await adapter.ApplyAsync(
            new MutationIntent(
    new OperationId(Guid.NewGuid()),
    EngineFixture.Account,
    new MessageId(1),
    identity,
    MutationKind.Move,
    false,
    "INBOX"
),
            CancellationToken.None
        );

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(server.Commands.Any(line => line.Contains("COPY", StringComparison.Ordinal)), Is.True);
        Assert.That(server.Commands.Any(line => line.Contains("EXPUNGE", StringComparison.Ordinal)), Is.False);
        Assert.That(server.Commands.Any(line => line.Contains("STORE", StringComparison.Ordinal)), Is.False);
    }

    [TestCase("café", true)]
    [TestCase("bad\r\nheader", false)]
    public async Task FreezeAsync_WhenSubjectIsValidated_PreservesUnicodeAndRejectsInjection(string subject, bool valid)
    {
        // Arrange
        MimePipeline pipeline = new MimePipeline(4096, 20);
        using MemoryStream target = new MemoryStream();

        // Act
        Outcome<bool> frozen = await pipeline.FreezeAsync(new ComposeContent(
            "sender@synthetic.invalid",
            ["recipient@synthetic.invalid"],
            subject,
            "Résumé plain text",
            DateTimeOffset.UnixEpoch,
            "id@synthetic.invalid"
        ), target, CancellationToken.None);
        target.Position = 0;
        Outcome<MimeSnapshot>? parsed = valid ? await pipeline.ParseAsync(target, CancellationToken.None) : null;

        // Assert
        Assert.That(frozen.IsSuccess, Is.EqualTo(valid));
        if (valid)
        {
            Assert.That(parsed!.Value!.Subject, Is.EqualTo(subject));
            Assert.That(parsed.Value.PlainText, Does.Contain("Résumé"));
        }
    }

    [Test]
    public async Task ParseAsync_WhenEncodedPayloadExceedsBudget_RejectsBoundedly()
    {
        // Arrange
        MimePipeline pipeline = new MimePipeline(100, 10);
        using MemoryStream source = new MemoryStream(Encoding.ASCII.GetBytes(
            "Content-Type: text/plain\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(new byte[1000])
        ));

        // Act
        Outcome<MimeSnapshot> result = await pipeline.ParseAsync(source, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }
}

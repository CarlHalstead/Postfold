using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;
using Postfold.Protocols;
using Postfold.Storage;
using Postfold.Sync;

namespace Postfold.Engine.Tests;

public sealed class IntegrationTests
{
    [TestCase(false, false, SubmissionState.Accepted)]
    [TestCase(true, false, SubmissionState.PartiallyAccepted)]
    [TestCase(false, true, SubmissionState.Uncertain)]
    public async Task ProcessOutboxAsync_WhenFrozenWorkIsReopened_PersistsWireEvidenceWithoutResubmission(
        bool partial, bool loseAcknowledgement, SubmissionState expected)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer incoming = new LocalMailServer(imap: true);
        await using LocalMailServer outgoing = new LocalMailServer(imap: false)
        {
            RejectSecondRecipient = partial,
            LoseAcknowledgement = loseAcknowledgement
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(EngineFixture.Account,
            new MailEndpoint("localhost", incoming.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials, 1048576)
        {
            FixtureAuthority = incoming.Authority
        };
        SmtpSubmitter submitter = new SmtpSubmitter(EngineFixture.Account,
            new MailEndpoint("localhost", outgoing.Port, TlsMode.Implicit, "synthetic", "fixture"), credentials)
        {
            FixtureAuthority = outgoing.Authority
        };
        EnginePolicy policy = new EnginePolicy(true, false, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        ImmutableArray<MailboxSnapshot> mailboxes = (await reader.DiscoverAsync(CancellationToken.None)).Value;
        await using MailAccountEngine engine = Create(reader, submitter, profile, scheduler, policy, retry, mailboxes);
        ComposeContent compose = new ComposeContent("sender@synthetic.invalid",
            ["first@synthetic.invalid", "second@synthetic.invalid"], "Frozen résumé", "synthetic body",
            DateTimeOffset.UnixEpoch, "frozen@synthetic.invalid");
        FrozenSubmission frozen = (await engine.QueueSubmissionAsync(compose, null, null, CancellationToken.None)).Value!;
        await profile.DisposeAsync();

        // Act
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await reopened.RecoverAsync(CancellationToken.None);
        await using MailAccountEngine recovered = Create(reader, submitter, reopened, scheduler, policy, retry, mailboxes);
        await recovered.ProcessOutboxAsync(CancellationToken.None);
        await recovered.ProcessOutboxAsync(CancellationToken.None);
        OutboxEntry? result = await ((IOutboxStore)reopened).ReadAsync(frozen.Id, frozen.Account, CancellationToken.None);
        await using Stream saved = await reopened.Blobs.OpenAsync(frozen.Mime, CancellationToken.None);
        using StreamReader text = new StreamReader(saved, Encoding.UTF8);
        string bytes = await text.ReadToEndAsync();

        // Assert
        Assert.That(result!.State, Is.EqualTo(expected));
        Assert.That(result.Evidence!.Recipients, Has.Length.EqualTo(2));
        Assert.That(outgoing.SubmittedMime, Is.EqualTo(bytes));
        Assert.That(outgoing.SubmissionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReconcileAsync_WhenCancelledAfterCommittedPage_ResumesAfterReopening()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer incoming = new LocalMailServer(imap: true);
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(EngineFixture.Account,
            new MailEndpoint("localhost", incoming.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials, 1048576)
        {
            FixtureAuthority = incoming.Authority
        };
        ImmutableArray<MailboxSnapshot> mailboxes = (await reader.DiscoverAsync(CancellationToken.None)).Value;
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        ReconciliationWork first = new ReconciliationWork(reader, profile, mailboxes[0], 1);
        first.Committed += (_, _) => cancellation.Cancel();
        await first.RunAsync(cancellation.Token);
        Checkpoint? partial = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);
        await profile.DisposeAsync();
        EnginePolicy policy = new EnginePolicy(true, true, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);

        // Act
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await using MailAccountEngine engine = Create(reader,
            new SmtpSubmitter(EngineFixture.Account,
                new MailEndpoint("localhost", 1, TlsMode.Implicit, "synthetic", "fixture"), credentials),
            reopened, scheduler, policy, retry, mailboxes);
        Failure resumed = await engine.ReconcileAsync("INBOX", CancellationToken.None);
        Checkpoint? complete = await reopened.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);
        Outcome<SearchPage> messages = await reopened.SearchAsync(new SearchRequest(EngineFixture.Account,
            false, "", null, false, 10), CancellationToken.None);

        // Assert
        Assert.That(partial!.Complete, Is.False);
        Assert.That(resumed, Is.EqualTo(Failure.None));
        Assert.That(complete!.Complete, Is.True);
        Assert.That(messages.Value!.Messages, Has.Length.EqualTo(2));
    }

    [Test]
    public async Task WaitForChangesAsync_WhenIdleSignalsChange_ReconcilesBeforeReturning()
    {
        // Arrange
        await using LocalMailServer incoming = new LocalMailServer(imap: true) { AdvancedImap = true };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        await using ImapAdapter reader = new ImapAdapter(EngineFixture.Account,
            new MailEndpoint("localhost", incoming.Port, TlsMode.Implicit, "synthetic", "fixture"),
            credentials, 1048576)
        {
            FixtureAuthority = incoming.Authority
        };
        ImmutableArray<MailboxSnapshot> mailboxes = (await reader.DiscoverAsync(CancellationToken.None)).Value;
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        EnginePolicy policy = new EnginePolicy(true, true, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        await using MailAccountEngine engine = Create(reader,
            new SmtpSubmitter(EngineFixture.Account,
                new MailEndpoint("localhost", 1, TlsMode.Implicit, "synthetic", "fixture"), credentials),
            profile, scheduler, policy, retry, mailboxes);

        // Act
        Failure result = await engine.WaitForChangesAsync("INBOX", TimeSpan.FromMinutes(1), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        Checkpoint? checkpoint = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(checkpoint!.Complete, Is.True);
        Assert.That(message.Provider.Imap!.UidValidity, Is.EqualTo(7));
    }

    private static MailAccountEngine Create(ImapAdapter reader, IMessageSubmitter submitter, SqliteProfile profile,
        IAccountScheduler scheduler, IWorkPolicy policy, IRetryDelayPolicy retry,
        ImmutableArray<MailboxSnapshot> mailboxes)
    {
        return new MailAccountEngine(reader, reader, reader, null, null, submitter, profile, profile, profile, profile,
            profile, profile.Blobs, new MimePipeline(1048576, 100, profile.Blobs), scheduler, policy, retry,
            TimeProvider.System, new EngineLimits(2, 8, 1, 1, 1048576, 100, 30, 2, 1048576), profile, mailboxes,
            reader.CreateIdleSignal());
    }
}

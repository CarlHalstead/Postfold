using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Policies;
using Postfold.Sync.Services;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Protocols;

public sealed class ClientProtocolFlowTests
{
    [AvaloniaTest]
    [TestCase(false, false, SubmissionState.Accepted)]
    [TestCase(true, false, SubmissionState.PartiallyAccepted)]
    [TestCase(false, true, SubmissionState.Uncertain)]
    public async Task QueueAsync_WhenClientUsesLocalTlsServers_PreservesDeliveryEvidenceAcrossRestart(
        bool partial,
        bool lostAcknowledgement,
        SubmissionState expected
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer incoming = new LocalMailServer(true);
        await using LocalMailServer outgoing = new LocalMailServer(false)
        {
            RejectSecondRecipient = partial,
            LoseAcknowledgement = lostAcknowledgement
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        MailEndpoint incomingEndpoint = new MailEndpoint("localhost", incoming.Port, TlsMode.Implicit, "synthetic", "fixture");
        MailEndpoint outgoingEndpoint = new MailEndpoint("localhost", outgoing.Port, TlsMode.Implicit, "synthetic", "fixture");
        await using ImapAdapter adapter = new ImapAdapter(EngineFixture.Account, incomingEndpoint, credentials, 1048576)
        {
            FixtureAuthority = incoming.Authority
        };
        SmtpSubmitter smtp = new SmtpSubmitter(EngineFixture.Account, outgoingEndpoint, credentials)
        {
            FixtureAuthority = outgoing.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.ConfigureAccountAsync(EngineFixture.Account, incomingEndpoint, outgoingEndpoint, CancellationToken.None);
        ImmutableArray<MailboxSnapshot> mailboxes = (await adapter.DiscoverAsync(CancellationToken.None)).Value;
        await profile.SaveMailboxesAsync(EngineFixture.Account, mailboxes, null, CancellationToken.None);
        EnginePolicy policy = new EnginePolicy(true, false, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        await using MailAccountEngine engine = new MailAccountEngine(
            adapter,
            adapter,
            adapter,
            null,
            null,
            smtp,
            profile,
            profile,
            profile,
            profile,
            profile,
            profile.Blobs,
            mime,
            scheduler,
            policy,
            retry,
            TimeProvider.System,
            new EngineLimits(2, 8, 1, 100, 1048576, 100, 0, 0, 10485760),
            profile,
            mailboxes
        );
        IAccountEngines lookup = Substitute.For<IAccountEngines>();
        lookup.Find(EngineFixture.Account).Returns(engine);
        MailApplication application = new MailApplication(
            profile,
            profile,
            profile.Blobs,
            mime,
            lookup,
            1048576,
            mime,
            profile,
            profile,
            profile,
            profile
        );
        ComposeContent content = new ComposeContent(
            "sender@synthetic.invalid",
            ["first@synthetic.invalid", "second@synthetic.invalid"],
            "Client synthetic send",
            "Preserved client text",
            DateTimeOffset.UnixEpoch,
            "client@synthetic.invalid",
            Signature: "Synthetic signature"
        );
        ComposeWindow compose = new ComposeWindow(application, EngineFixture.Account, content);
        compose.Show();
        try
        {
            // Act
            await compose.QueueAsync();
            await application.ProcessOutboxAsync(EngineFixture.Account, CancellationToken.None);
            await application.ProcessOutboxAsync(EngineFixture.Account, CancellationToken.None);
            OutboxEntry entry = (await profile.OutboxAsync(EngineFixture.Account, CancellationToken.None))[0];
            await application.StoreSentCopyAsync(entry, CancellationToken.None);
            await using SqliteProfile restarted = (await SqliteProfile.OpenAsync(
                System.IO.Path.Combine(directory, "profile.db"),
                TimeProvider.System,
                CancellationToken.None
            )).Value!;
            await restarted.RecoverAsync(CancellationToken.None);
            OutboxEntry preserved = (await restarted.OutboxAsync(EngineFixture.Account, CancellationToken.None))[0];

            // Assert
            Assert.That(compose.IsVisible, Is.False);
            Assert.That(entry.State, Is.EqualTo(expected));
            Assert.That(preserved.State, Is.EqualTo(expected));
            Assert.That(preserved.Evidence!.Recipients, Has.Length.EqualTo(2));
            Assert.That(outgoing.SubmissionCount, Is.EqualTo(1));
            Assert.That(outgoing.SubmittedMime, Does.Contain("Preserved client text"));
            Assert.That(outgoing.SubmittedMime, Does.Contain("Synthetic signature"));
        }
        finally
        {
            compose.ConfirmShutdown();
            compose.Close();
        }
    }
}

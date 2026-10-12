using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Policies;
using Postfold.Sync.Services;
using Postfold.Sync.Stores;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class ClientDraftConflictTests
{
    [AvaloniaTest]
    public async Task PushDraftAsync_WhenIncompleteClientDraftConflicts_PreservesBothVersionsAcrossRestartAndHoldsSend()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer server = new LocalMailServer(true)
        {
            AdvancedImap = true
        };
        SessionCredentialStore credentials = new SessionCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        MailEndpoint endpoint = new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture");
        await using ImapAdapter adapter = new ImapAdapter(EngineFixture.Account, endpoint, credentials, 1048576)
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
        ImmutableArray<MailboxSnapshot> mailboxes = (await adapter.DiscoverAsync(CancellationToken.None)).Value;
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        IRemoteDraftStore remote = new ComposeRemoteDraftStore(adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!, mime);
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();
        EnginePolicy policy = new EnginePolicy(true, false, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        await using MailAccountEngine engine = new MailAccountEngine(
            adapter,
            adapter,
            adapter,
            remote,
            null,
            submitter,
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
        IAccountEngines engines = Substitute.For<IAccountEngines>();
        engines.Find(EngineFixture.Account).Returns(engine);
        MailApplication application = new MailApplication(
            profile,
            profile,
            profile.Blobs,
            mime,
            engines,
            1048576,
            mime,
            profile,
            profile,
            profile,
            profile
        );
        Guid id = Guid.NewGuid();
        ComposeContent content = new ComposeContent(
            "sender@synthetic.invalid",
            [],
            "Incomplete local draft",
            "Preserve local text",
            DateTimeOffset.UnixEpoch,
            "draft@synthetic.invalid",
            Signature: "Local signature"
        );
        ComposeWindow compose = new ComposeWindow(application, EngineFixture.Account, content, id);
        compose.Show();
        await compose.FlushAsync();
        compose.ConfirmShutdown();
        compose.Close();
        DraftSnapshot saved = (await profile.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;
        ProviderIdentity identity = new ProviderIdentity(
            EngineFixture.Account,
            "remote",
            new ImapIdentity(EngineFixture.Account, "INBOX", 7, 1)
        );
        await profile.AcknowledgeAsync(
            saved,
            new RemoteDraftResult(identity, "prior-version", null, false, Failure.None),
            CancellationToken.None
        );
        await application.SaveDraftAsync(EngineFixture.Account, id, content, CancellationToken.None);

        // Act
        Failure pushed = await application.PushDraftAsync(EngineFixture.Account, id, CancellationToken.None);
        await using SqliteProfile restarted = (await SqliteProfile.OpenAsync(
            Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;
        DraftSnapshot preserved = (await restarted.DraftsAsync(EngineFixture.Account, CancellationToken.None))[0];
        ImmutableArray<DraftVersionSnapshot> versions = await restarted.DraftVersionsAsync(
            EngineFixture.Account,
            id,
            CancellationToken.None
        );
        ComposeContent local = (await application.ReadDraftAsync(preserved, CancellationToken.None)).Value!;
        DraftVersionSnapshot remoteVersion = versions.Single(version => version.Origin == "Remote");
        ComposeContent remoteContent = (await application.ReadDraftAsync(
            preserved with
            {
                Content = remoteVersion.Content
            },
            CancellationToken.None
        )).Value!;
        ComposeWindow held = new ComposeWindow(application, EngineFixture.Account, local, id, true);
        held.Show();
        try
        {
            await held.QueueAsync();

            // Assert
            Assert.That(pushed, Is.EqualTo(Failure.NeedsReview));
            Assert.That(preserved.Conflict, Is.True);
            Assert.That(local.PlainText, Is.EqualTo(content.PlainText));
            Assert.That(local.Signature, Is.EqualTo(content.Signature));
            Assert.That(remoteContent.PlainText, Is.EqualTo("synthetic body\r\n"));
            Assert.That(versions.Count(version => version.Origin == "Local"), Is.GreaterThanOrEqualTo(2));
            Assert.That(held.IsVisible, Is.True);
            Assert.That(await restarted.OutboxAsync(EngineFixture.Account, CancellationToken.None), Is.Empty);
            Assert.That(server.Commands.Any(command => command.Contains("APPEND", StringComparison.Ordinal)), Is.False);
            await submitter.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default);
        }
        finally
        {
            held.ConfirmShutdown();
            held.Close();
        }
    }
}

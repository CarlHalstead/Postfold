using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Policies;
using Postfold.Sync.Services;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class ClientAttachmentTests
{
    [AvaloniaTest]
    public async Task SaveAttachmentAsync_WhenOnlyMetadataIsCached_StreamsSelectedPartAndRejectsRecycledUid()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer server = new LocalMailServer(true)
        {
            AttachmentMessage = true
        };
        InMemoryCredentialStore credentials = new InMemoryCredentialStore();
        await credentials.WriteAsync("fixture", new Credential("synthetic", false), CancellationToken.None);
        MailEndpoint endpoint = new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "fixture");
        await using ImapAdapter adapter = new ImapAdapter(EngineFixture.Account, endpoint, credentials, 1048576)
        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        BlobReference body = await EngineFixture.BlobAsync(profile, "Content-Type: text/plain\r\n\r\ncached text\r\n", false);
        await profile.StoreBodyAsync(message.Account, message.Id, body, "cached text", CancellationToken.None);
        ImmutableArray<MailboxSnapshot> mailboxes = (await adapter.DiscoverAsync(CancellationToken.None)).Value;
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
            Substitute.For<IMessageSubmitter>(),
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
        engines.Find(message.Account).Returns(engine);
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

        // Act
        Outcome<ImmutableArray<AttachmentSnapshot>> discovered = await application.ReadAttachmentsAsync(
            message,
            true,
            CancellationToken.None
        );
        using MemoryStream selectedDestination = new MemoryStream();
        Failure saved = await application.SaveAttachmentAsync(
            message,
            discovered.Value[0].Part,
            selectedDestination,
            CancellationToken.None
        );
        Guid draftId = Guid.NewGuid();
        ComposeWindow forward = new ComposeWindow(
            application,
            message.Account,
            new ComposeContent(
    "sender@synthetic.invalid",
    [],
    "Forward",
    "Quoted text",
    DateTimeOffset.UnixEpoch,
    "forward@synthetic.invalid"
),
            draftId
        );
        forward.Show();
        Failure attached;
        try
        {
            attached = await forward.AttachOriginalAsync(message, discovered.Value[0], CancellationToken.None);
        }
        finally
        {
            forward.ConfirmShutdown();
            forward.Close();
        }
        server.UidValidity = 8;
        using MemoryStream staleDestination = new MemoryStream();
        Failure stale = await application.SaveAttachmentAsync(
            message,
            discovered.Value[0].Part,
            staleDestination,
            CancellationToken.None
        );

        await profile.DisposeAsync();
        await using SqliteProfile restarted = (await SqliteProfile.OpenAsync(
            Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;
        ImmutableArray<AttachmentSnapshot> cached = await restarted.ReadAttachmentsAsync(
            message.Account,
            message.Id,
            CancellationToken.None
        );
        DraftSnapshot draft = (await restarted.DraftsAsync(message.Account, CancellationToken.None))[0];
        ComposeContent restored = (await application.ReadDraftAsync(draft, CancellationToken.None)).Value!;

        // Assert
        Assert.That(discovered.IsSuccess, Is.True);
        Assert.That(cached, Has.Length.EqualTo(1));
        Assert.That(cached[0].Name, Is.EqualTo("report.bin"));
        Assert.That(cached[0].Availability, Is.EqualTo(ContentAvailability.Missing));
        Assert.That(saved, Is.EqualTo(Failure.None));
        Assert.That(attached, Is.EqualTo(Failure.None));
        Assert.That(restored.Attachments, Has.Length.EqualTo(1));
        Assert.That(restored.Attachments[0].Content.Protected, Is.True);
        Assert.That(Encoding.UTF8.GetString(selectedDestination.ToArray()), Is.EqualTo("synthetic attachment"));
        Assert.That(server.Commands.Any(command => command.Contains("BODY.PEEK[2]", StringComparison.Ordinal)), Is.True);
        Assert.That(server.Commands.Any(command => command.Contains("BODY.PEEK[]", StringComparison.Ordinal)), Is.False);
        Assert.That(stale, Is.EqualTo(Failure.NeedsReview));
        Assert.That(staleDestination.Length, Is.Zero);
    }
}

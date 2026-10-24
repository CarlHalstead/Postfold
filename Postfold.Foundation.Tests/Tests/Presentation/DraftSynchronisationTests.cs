using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Foundation.Tests.Fixtures.Desktop;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Stores;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class DraftSynchronisationTests
{
    [AvaloniaTest]
    public async Task PrepareShutdownAsync_WhenVersionCheckpointFails_KeepsComposeOpenWithDurableWorkingText()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        QualificationMailApplication application = new QualificationMailApplication(runtime.Application)

        {
            FailDraftCloses = true
        };
        ComposeWindow window = new ComposeWindow(application, EngineFixture.Account, Content());
        window.Show();
        try
        {
            Assert.That(await window.FlushAsync(), Is.True);

            // Act
            bool closed = await window.PrepareShutdownAsync();

            // Assert
            Assert.That(closed, Is.False);
            Assert.That(window.IsVisible, Is.True);
            DraftSnapshot working = (await runtime.Catalog.DraftsAsync(window.Account, CancellationToken.None)).Single(item => item.Id == window.DraftId);
            Assert.That((await runtime.Application.ReadDraftAsync(working, CancellationToken.None)).Value!.PlainText, Is.EqualTo(Content().PlainText));
            Assert.That(working.VersionCount, Is.Zero);
            Assert.That(provider.DraftWrites + provider.DraftUpdates, Is.Zero);
        }
        finally
        {
            window.ConfirmShutdown();
            window.Close();
        }
    }

    [Test]
    public async Task ReadDraftPageAsync_WhenImapDraftsAreRead_UsesExistingTlsReaderAndProtectedMimeWithoutRemoteWrites()
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(true)

        {
            AdvancedImap = true
        };
        InMemoryCredentialStore credentials = new InMemoryCredentialStore();
        await credentials.WriteAsync("draft-fixture", new Credential("synthetic", false), CancellationToken.None);
        MailEndpoint endpoint = new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "draft-fixture");
        await using ImapAdapter adapter = new ImapAdapter(EngineFixture.Account, endpoint, credentials, 1048576)

        {
            FixtureAuthority = server.Authority
        };
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await adapter.DiscoverAsync(CancellationToken.None);
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        IRemoteDraftStore selected = adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!;
        IRemoteDraftSource source = new ComposeRemoteDraftStore(selected, mime);

        // Act
        Outcome<RemoteDraftPage> page = await source.ReadDraftPageAsync(null, 1, CancellationToken.None);
        Assert.That(page.IsSuccess, Is.True);
        RemoteDraftResult remote = page.Value!.Drafts.Single();
        await using Stream content = await profile.Blobs.OpenAsync(remote.ConflictingContent!, CancellationToken.None);
        ComposeContent parsed = (await mime.ReadComposeAsync(EngineFixture.Account, content, CancellationToken.None)).Value!;
        Outcome<RemoteDraftPage> next = await source.ReadDraftPageAsync(page.Value.Next, 1, CancellationToken.None);

        // Assert
        Assert.That(page.Value.Next, Is.Not.Null);
        Assert.That(next.IsSuccess, Is.True);
        Assert.That(remote.Identity!.Imap!.Uid, Is.Not.EqualTo(next.Value!.Drafts.Single().Identity!.Imap!.Uid));
        Assert.That(remote.ConflictingContent!.Protected, Is.True);
        Assert.That(parsed.PlainText, Does.Contain("synthetic body"));
        Assert.That(server.Commands.Any(command => command.Contains(" APPEND ", StringComparison.Ordinal) || command.Contains(" STORE ", StringComparison.Ordinal)), Is.False);
    }

    [Test]
    public async Task SaveDraftAsync_WhenAutosaveRepeats_PreservesWorkingEditsWithoutCreatingClosedVersions()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        Guid id = Guid.NewGuid();
        ComposeContent content = Content();
        DraftSnapshot draft = (await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, content, CancellationToken.None)).Value!;

        // Act
        for (int index = 1; index <= 4; index++)
        {
            content = content with { PlainText = "Working edit " + index };
            draft = (await runtime.Application.SaveDraftRevisionAsync(draft, content, CancellationToken.None)).Value!;
        }
        DraftSnapshot unchanged = (await runtime.Application.SaveDraftRevisionAsync(draft, content, CancellationToken.None)).Value!;
        Assert.That(await runtime.Catalog.DraftVersionsAsync(draft.Account, id, CancellationToken.None), Is.Empty);
        Assert.That(draft.VersionCount, Is.Zero);
        DraftSnapshot closed = (await runtime.Application.CloseDraftAsync(draft.Account, id, false, CancellationToken.None)).Value!;
        DraftSnapshot closedAgain = (await runtime.Application.CloseDraftAsync(draft.Account, id, false, CancellationToken.None)).Value!;
        DraftSnapshot working = (await runtime.Application.SaveDraftRevisionAsync(closed, content with { PlainText = "Next open edit" }, CancellationToken.None)).Value!;
        DraftSnapshot finalized = (await ((IDraftStore)runtime.Catalog).ReadFinalizedAsync(id, draft.Account, CancellationToken.None))!;

        // Assert
        Assert.That(unchanged.Revision, Is.EqualTo(draft.Revision));
        Assert.That(closed.VersionCount, Is.EqualTo(1));
        Assert.That(closedAgain, Is.EqualTo(closed));
        Assert.That(working.VersionCount, Is.EqualTo(1));
        Assert.That(finalized.Revision, Is.EqualTo(closed.Revision));
        Assert.That((await runtime.Application.ReadDraftAsync(finalized, CancellationToken.None)).Value!.PlainText, Is.EqualTo("Working edit 4"));
        Assert.That((await runtime.Application.ReadDraftAsync(working, CancellationToken.None)).Value!.PlainText, Is.EqualTo("Next open edit"));
        Assert.That(provider.DraftWrites, Is.Zero);
        Assert.That(provider.DraftUpdates, Is.Zero);
    }

    [AvaloniaTest]
    public async Task PrepareShutdownAsync_WhenComposeCloses_CreatesOneVersionAndAutomaticallyUpdatesTheSameServerDraft()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        using MemoryStream bytes = new MemoryStream(Encoding.UTF8.GetBytes("attachment survives ordinary sync"));
        ComposeAttachment attachment = (await runtime.Application.ImportAttachmentAsync(EngineFixture.Account, "ordinary.txt", bytes, CancellationToken.None)).Value!;
        ComposeContent content = Content() with { Attachments = [attachment] };
        ComposeWindow first = new ComposeWindow(runtime.Application, EngineFixture.Account, content);
        first.Show();
        try
        {
            await first.FlushAsync();
            for (int index = 0; index < 3; index++)
            {
                first.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "Body").Text = "First session " + index;
                await first.FlushAsync();
            }
            Assert.That(await runtime.Catalog.DraftVersionsAsync(first.Account, first.DraftId, CancellationToken.None), Is.Empty);

            // Act
            Assert.That(await first.PrepareShutdownAsync(), Is.True);
            first.ConfirmShutdown();
            first.Close();
            await runtime.Application.WaitForDraftSyncAsync(first.Account, first.DraftId, CancellationToken.None);
            DraftSnapshot saved = (await runtime.Catalog.DraftsAsync(first.Account, CancellationToken.None)).Single(item => item.Id == first.DraftId);
            ComposeContent restored = (await runtime.Application.ReadDraftAsync(saved, CancellationToken.None)).Value!;
            ComposeWindow second = new ComposeWindow(runtime.Application, saved.Account, restored, saved.Id, draft: saved);
            second.Show();
            try
            {
                second.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "Body").Text = "Second session";
                await second.FlushAsync();
                await second.FlushAsync();
                Assert.That(await second.PrepareShutdownAsync(), Is.True);
                second.ConfirmShutdown();
                second.Close();
                await runtime.Application.WaitForDraftSyncAsync(saved.Account, saved.Id, CancellationToken.None);

                // Assert
                DraftSnapshot latest = (await runtime.Catalog.DraftsAsync(saved.Account, CancellationToken.None)).Single(item => item.Id == saved.Id);
                Assert.That(latest.VersionCount, Is.EqualTo(2));
                Assert.That(latest.Remote, Is.EqualTo(saved.Remote));
                Assert.That(latest.PushedRevision, Is.EqualTo(latest.Revision));
                Assert.That(latest.Held || latest.Conflict, Is.False);
                Assert.That((await runtime.Application.ReadDraftAsync(latest, CancellationToken.None)).Value!.Attachments, Is.EqualTo(restored.Attachments));
                Assert.That(provider.DraftWrites, Is.EqualTo(1));
                Assert.That(provider.DraftUpdates, Is.EqualTo(1));
                Assert.That(provider.Sends, Is.Zero);
            }
            finally
            {
                second.ConfirmShutdown();
                second.Close();
            }
        }
        finally
        {
            first.ConfirmShutdown();
            first.Close();
        }
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenDraftWasCreatedElsewhere_ImportsItAndUpdatesItsExistingServerIdentity()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        provider.ReplaceDraft("external-draft", GmailFixture.Mime + "Created in another client");
        IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;

        // Act
        Assert.That(await engine.SynchroniseDraftsAsync(CancellationToken.None), Is.EqualTo(Failure.None));
        DraftSnapshot imported = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        ComposeContent content = (await runtime.Application.ReadDraftAsync(imported, CancellationToken.None)).Value!;
        await runtime.Application.SaveDraftRevisionAsync(imported, content with { PlainText = "Edited in Postfold" }, CancellationToken.None);
        await runtime.Application.CloseDraftAsync(engine.Account, imported.Id, false, CancellationToken.None);
        Assert.That(await engine.SynchroniseDraftsAsync(CancellationToken.None), Is.EqualTo(Failure.None));

        // Assert
        DraftSnapshot saved = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        Assert.That(content.PlainText, Does.Contain("Created in another client"));
        Assert.That(imported.VersionCount, Is.Zero);
        Assert.That(saved.VersionCount, Is.EqualTo(1));
        Assert.That(saved.Remote!.Key, Is.EqualTo("external-draft"));
        Assert.That(saved.PushedRevision, Is.EqualTo(saved.Revision));
        Assert.That(provider.DraftWrites, Is.Zero);
        Assert.That(provider.DraftUpdates, Is.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenServerChangesDuringAnOpenEditor_PreservesBothEditsAndRequiresConflictReview()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        provider.ReplaceDraft("external-draft", GmailFixture.Mime);
        IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;
        await engine.SynchroniseDraftsAsync(CancellationToken.None);
        DraftSnapshot opened = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        ComposeContent original = (await runtime.Application.ReadDraftAsync(opened, CancellationToken.None)).Value!;
        provider.ReplaceDraft("external-draft", GmailFixture.Mime + "new server edit");

        // Act
        await engine.SynchroniseDraftsAsync(CancellationToken.None);
        DraftSnapshot refreshed = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        Assert.That((await runtime.Application.ReadDraftAsync(refreshed, CancellationToken.None)).Value!.PlainText, Does.Contain("new server edit"));
        DraftSnapshot edited = (await runtime.Application.SaveDraftRevisionAsync(opened, original with { PlainText = "concurrent editor text" }, CancellationToken.None)).Value!;
        await runtime.Application.CloseDraftAsync(engine.Account, opened.Id, false, CancellationToken.None);
        await engine.SynchroniseDraftsAsync(CancellationToken.None);

        // Assert
        Assert.That(edited.Conflict && edited.Held, Is.True);
        Assert.That((await runtime.Application.ReadDraftAsync(edited, CancellationToken.None)).Value!.PlainText, Is.EqualTo("concurrent editor text"));
        Assert.That(await runtime.Catalog.DraftVersionsAsync(engine.Account, opened.Id, CancellationToken.None), Has.Length.GreaterThanOrEqualTo(3));
        Assert.That(provider.DraftWrites + provider.DraftUpdates, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenWorkingDraftRemainsOpen_DoesNotPublishAutosavesAsClosedVersions()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;
        ComposeContent content = Content();
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(engine.Account, id, content, CancellationToken.None);
        await runtime.Application.CloseDraftAsync(engine.Account, id, false, CancellationToken.None);
        await engine.SynchroniseDraftsAsync(CancellationToken.None);
        DraftSnapshot closed = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single(item => item.Id == id);

        // Act
        await runtime.Application.SaveDraftRevisionAsync(closed, content with { PlainText = "still typing" }, CancellationToken.None);
        await engine.SynchroniseDraftsAsync(CancellationToken.None);

        // Assert
        DraftSnapshot current = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(current.VersionCount, Is.EqualTo(1));
        Assert.That(current.FinalizedRevision, Is.EqualTo(closed.Revision));
        Assert.That(current.PushedRevision, Is.EqualTo(closed.Revision));
        Assert.That(current.Revision, Is.GreaterThan(closed.Revision));
        Assert.That(current.Conflict || current.Held, Is.False);
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.DraftUpdates, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenClosedDraftReopensAfterOfflineShutdown_PublishesItOnceWithoutManualSave()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        Guid id = Guid.NewGuid();
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
            await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
            Assert.That(provider.DraftWrites, Is.Zero);
        }

        // Act
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        await reopened.Application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);
        await reopened.Application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        DraftSnapshot draft = (await reopened.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(draft.VersionCount, Is.EqualTo(1));
        Assert.That(draft.PushedRevision, Is.EqualTo(draft.Revision));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.DraftUpdates, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenAuthenticationRejectsBeforeCreate_RetainsPendingVersionAndCanRetryWithoutAnUncertaintyHold()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
        await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
        provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Post ?
            GmailFixture.Reply("{}", HttpStatusCode.Unauthorized) : null);

        // Act
        Assert.That(await runtime.Find(EngineFixture.Account)!.SynchroniseDraftsAsync(CancellationToken.None), Is.EqualTo(Failure.NeedsAuthentication));
        DraftSnapshot pending = (await runtime.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single();
        provider.Intercept = null;
        Assert.That(await runtime.Find(EngineFixture.Account)!.SynchroniseDraftsAsync(CancellationToken.None), Is.EqualTo(Failure.None));

        // Assert
        Assert.That(pending.Held, Is.False);
        Assert.That(pending.VersionCount, Is.EqualTo(1));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenUpdateAcknowledgementIsLost_DoesNotRepeatPutAfterRestart()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        Guid id = Guid.NewGuid();
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
            await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
            await runtime.Find(EngineFixture.Account)!.SynchroniseDraftsAsync(CancellationToken.None);
            DraftSnapshot saved = (await runtime.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single();
            await runtime.Application.SaveDraftRevisionAsync(saved, Content() with { PlainText = "possibly updated" }, CancellationToken.None);
            await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
            provider.LoseDraftAcknowledgement = true;
            Assert.That(await runtime.Find(EngineFixture.Account)!.SynchroniseDraftsAsync(CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        }

        // Act
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        await reopened.Application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);
        await reopened.Application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        DraftSnapshot held = (await reopened.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(held.Held, Is.True);
        Assert.That(held.VersionCount, Is.EqualTo(2));
        Assert.That(held.PushedRevision, Is.LessThan(held.Revision));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.DraftUpdates, Is.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task CloseDraftAsync_WhenShutdownInterruptsAutomaticCreate_DrainsOwnedWorkAndRetainsDurableUncertainty()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        Guid id = Guid.NewGuid();
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
            provider.DraftEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Act
            Assert.That((await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, true, CancellationToken.None)).IsSuccess, Is.True);
            await provider.DraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await runtime.DisposeAsync();
        }
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        await reopened.Application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        DraftSnapshot held = (await reopened.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(held.Held, Is.True);
        Assert.That(held.VersionCount, Is.EqualTo(1));
        Assert.That(held.PushedRevision, Is.Zero);
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenGmailNormalizesCreatedMime_UsesAcknowledgedServerBytesForTheNextConflictCheck()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        bool normalized = false;
        provider.Intercept = (request, _) =>
        {
            if (!normalized && provider.DraftWrites == 1 && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/drafts/draft-1", StringComparison.Ordinal))
            {
                provider.NormalizeDraft("draft-1");
                normalized = true;
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
        await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
        IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;

        // Act
        await engine.SynchroniseDraftsAsync(CancellationToken.None);
        DraftSnapshot first = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        await runtime.Application.SaveDraftRevisionAsync(first, Content() with { PlainText = "next ordinary version" }, CancellationToken.None);
        await runtime.Application.CloseDraftAsync(engine.Account, id, false, CancellationToken.None);
        Failure result = await engine.SynchroniseDraftsAsync(CancellationToken.None);

        // Assert
        DraftSnapshot latest = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        Assert.That(normalized, Is.True);
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(latest.Held || latest.Conflict, Is.False);
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.DraftUpdates, Is.EqualTo(1));
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenServerChangesAfterUpdateAcknowledgement_DoesNotAcknowledgeThatNewRemoteVersionAsOurEdit()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
        await runtime.Application.CloseDraftAsync(EngineFixture.Account, id, false, CancellationToken.None);
        IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;
        await engine.SynchroniseDraftsAsync(CancellationToken.None);
        DraftSnapshot first = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
        await runtime.Application.SaveDraftRevisionAsync(first, Content() with { PlainText = "our closed edit" }, CancellationToken.None);
        await runtime.Application.CloseDraftAsync(engine.Account, id, false, CancellationToken.None);
        provider.Intercept = (request, _) =>
        {
            if (provider.DraftUpdates == 1 && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/drafts/draft-1", StringComparison.Ordinal))
            {
                provider.ReplaceDraft("draft-1", GmailFixture.Mime + "edit after acknowledgement");
            }
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        // Act
        Failure result = await engine.SynchroniseDraftsAsync(CancellationToken.None);

        // Assert
        DraftSnapshot held = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(held.Conflict && held.Held, Is.True);
        Assert.That(held.PushedRevision, Is.EqualTo(first.PushedRevision));
        Assert.That((await runtime.Application.ReadDraftAsync(held, CancellationToken.None)).Value!.PlainText, Is.EqualTo("our closed edit"));
        Assert.That(provider.DraftUpdates, Is.EqualTo(1));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
    }

    [Test]
    public async Task CloseDraftAsync_WhenNewAutosaveArrivesDuringCreate_AcknowledgesOnlyClosedContentAndLeavesWorkingEditsLocal()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        Guid id = Guid.NewGuid();
        DraftSnapshot draft = (await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None)).Value!;
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Intercept = async (request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/drafts", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        };
        await runtime.Application.CloseDraftAsync(draft.Account, id, true, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await runtime.Application.SaveDraftRevisionAsync(draft, Content() with { PlainText = "new open editor text" }, CancellationToken.None);
            release.TrySetResult();
            await runtime.Find(draft.Account)!.SynchroniseDraftsAsync(CancellationToken.None);

            // Assert
            DraftSnapshot current = (await runtime.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Single(item => item.Id == id);
            Assert.That(current.PushedRevision, Is.EqualTo(draft.Revision));
            Assert.That(current.FinalizedRevision, Is.EqualTo(draft.Revision));
            Assert.That(current.VersionCount, Is.EqualTo(1));
            Assert.That(current.Revision, Is.GreaterThan(draft.Revision));
            Assert.That((await runtime.Application.ReadDraftAsync(current, CancellationToken.None)).Value!.PlainText, Is.EqualTo("new open editor text"));
            Assert.That(current.Held || current.Conflict, Is.False);
            Assert.That(provider.DraftWrites, Is.EqualTo(1));
            Assert.That(provider.DraftUpdates, Is.Zero);
        }
        finally
        {
            release.TrySetResult();
            await runtime.Find(draft.Account)!.SynchroniseDraftsAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task SynchroniseDraftsAsync_WhenRemotePagesRepeat_StopsWithoutDeletingDraftsOrPublishingOpenEdits()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(), CancellationToken.None);
        provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/drafts", StringComparison.Ordinal) ?
            GmailFixture.Reply("{\"drafts\":[],\"nextPageToken\":\"repeated\"}") : null);

        // Act
        Failure result = await runtime.Find(EngineFixture.Account)!.SynchroniseDraftsAsync(CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Validation));
        DraftSnapshot draft = (await runtime.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
        Assert.That(draft.VersionCount, Is.Zero);
        Assert.That(draft.Remote, Is.Null);
        Assert.That(provider.DraftWrites + provider.DraftUpdates + provider.Sends, Is.Zero);
    }

    private static ComposeContent Content()
    {
        return new ComposeContent(GmailFixture.Binding.Address, [], "Ordinary synced draft", "Initial text", DateTimeOffset.UnixEpoch, "ordinary@synthetic.invalid");
    }
}

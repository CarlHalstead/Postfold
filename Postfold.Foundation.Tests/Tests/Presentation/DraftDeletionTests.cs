using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Outbox;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class DraftDeletionTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SelectDraftsFolder_WhenDraftIsSyncedOrHeld_LoadsNormalFolderWithoutOpeningDialog(
        bool conflict
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        provider.Messages["one"] = ["DRAFT"];
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot draft = conflict ? await GmailDraftReviewFixture.HoldAsync(runtime, provider) : await SaveAsync(runtime, "Synced draft", true);
        await runtime.Application.SynchroniseAsync(draft.Account, CancellationToken.None);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            NavigationItemViewModel folder = model.Navigation.SelectMany(item => item.DescendantsAndSelf()).Single(item =>
                item.Scope.Account == draft.Account && item.Scope.Folder == model.Mailboxes.Single(mailbox => mailbox.Account == draft.Account && mailbox.Role == MailboxRole.Drafts).Id);

            // Act
            main.FindControl<TreeView>("ScopeList")!.SelectedItem = folder;
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.Scope!.Folder, Is.EqualTo(folder.Scope.Folder));
            Assert.That(model.Rows.Select(item => item.Message.Provider.Key), Is.EqualTo(new[] { "one" }));
            Assert.That(main.OwnedWindows, Is.Empty);
            if (conflict)
            {
                await main.DraftsAsync(draft.Account);
                Window manager = main.OwnedWindows.Single(item => item.Title == Strings.Drafts);
                manager.GetVisualDescendants().OfType<Button>().Single(item => item.Name == "ReviewVersions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await main.WaitForActionsAsync();
                Assert.That(main.OwnedWindows.Any(item => item.Title == Strings.Get("ReviewVersions")), Is.True);
            }
        }
        finally
        {
            await main.FlushComposeAsync();
            main.Close();
            await FormControls.DrainAsync(main);
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DraftsAsync_WhenRowsHaveActions_UsesNamedPencilAndCrossAndDeletesOnlyItsRow(
        bool dark
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot first = await SaveAsync(runtime, "First draft", false);
        DraftSnapshot second = await SaveAsync(runtime, "Second draft", false);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow

        {
            DataContext = model,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            // Act
            await main.DraftsAsync(first.Account);
            Window drafts = main.OwnedWindows.Single(item => item.Title == Strings.Drafts);
            drafts.Width = dark ? 360 : 760;
            Button[] buttons = drafts.GetVisualDescendants().OfType<Button>().Where(item => item.Name is "OpenDraft" or "DeleteDraft").ToArray();
            Button remove = buttons.Single(item => AutomationProperties.GetName(item) == Strings.Get("DeleteDraft") + ": First draft");
            buttons.Single(item => AutomationProperties.GetName(item) == Strings.Get("OpenDraft") + ": Second draft").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FormControls.DrainAsync(main);
            buttons.Single(item => AutomationProperties.GetName(item) == Strings.Get("DeleteDraft") + ": Second draft").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FormControls.DrainAsync(main);
            Assert.That(await runtime.Catalog.DraftsAsync(first.Account, CancellationToken.None), Has.Length.EqualTo(2));
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await FormControls.DrainAsync(main);

            // Assert
            Assert.That(buttons, Has.Length.EqualTo(4));
            Assert.That(buttons.All(item => item.Content is Avalonia.Controls.Shapes.Path && item.MinWidth >= 40 && item.MinHeight >= 40), Is.True);
            Assert.That((await runtime.Catalog.DraftsAsync(first.Account, CancellationToken.None)).Select(item => item.Id), Is.EqualTo(new[] { second.Id }));
            Assert.That(provider.DraftDeletes + provider.DraftWrites + provider.Sends, Is.Zero);
        }
        finally
        {
            await main.FlushComposeAsync();
            main.Close();
            await FormControls.DrainAsync(main);
        }
    }

    [Test]
    public async Task DeleteDraftAsync_WhenOffline_RetainsIntentAcrossRestartAndRemovesTheServerDraftOnNormalSync()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        DraftSnapshot draft;
        await using (DesktopMailRuntime online = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            draft = await SaveAsync(online, "Offline deletion", true);
        }
        await using (DesktopMailRuntime offline = await GmailDraftReviewFixture.OpenAsync(directory, provider, false))
        {
            // Act
            Assert.That(await offline.Application.DeleteDraftAsync(draft, CancellationToken.None), Is.EqualTo(Failure.None));
            DraftSnapshot pending = (await offline.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Single();
            Assert.That(pending.DeletePending, Is.True);
            Assert.That((await offline.Application.SaveDraftRevisionAsync(pending, Content("Do not resurrect"), CancellationToken.None)).Failure, Is.EqualTo(Failure.NeedsReview));
            Assert.That(provider.DraftDeletes, Is.Zero);
        }
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        Assert.That(await reopened.Application.SynchroniseAsync(draft.Account, CancellationToken.None), Is.EqualTo(Failure.None));
        await reopened.Application.SynchroniseAsync(draft.Account, CancellationToken.None);

        // Assert
        Assert.That(await reopened.Catalog.DraftsAsync(draft.Account, CancellationToken.None), Is.Empty);
        Assert.That(provider.DraftDeletes, Is.EqualTo(1));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task DeleteDraftAsync_WhenAcknowledgementIsLost_RetriesOnlyTheSameIdentityAndAcceptsNotFound()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot draft = await SaveAsync(runtime, "Lost delete acknowledgement", true);
        DraftSnapshot other = await SaveAsync(runtime, "Keep this draft", true);
        provider.LoseDraftAcknowledgement = true;

        // Act
        Assert.That(await runtime.Application.DeleteDraftAsync(draft, CancellationToken.None), Is.EqualTo(Failure.Transient));
        Assert.That((await runtime.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Single(item => item.Id == draft.Id).DeletePending, Is.True);
        Assert.That(await runtime.Application.SynchroniseAsync(draft.Account, CancellationToken.None), Is.EqualTo(Failure.None));

        // Assert
        Assert.That((await runtime.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Select(item => item.Id), Is.EqualTo(new[] { other.Id }));
        Assert.That(provider.DraftDeletes, Is.EqualTo(2));
        Assert.That(provider.Requests.Where(item => item.Method == "DELETE").Select(item => item.Path).Distinct().ToArray(), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task RequestDeleteAsync_WhenSnapshotIsStaleForeignOrFrozen_LeavesDraftAndSubmissionEvidenceUnchanged()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot draft = await SaveAsync(runtime, "Protected work", false);
        IDraftStore store = (IDraftStore)runtime.Catalog;

        // Act
        Assert.That(await store.RequestDeleteAsync(draft with { Revision = draft.Revision + 1 }, CancellationToken.None), Is.False);
        Assert.That(await store.RequestDeleteAsync(draft with { Account = new AccountId(2) }, CancellationToken.None), Is.False);
        FrozenSubmission submission = EngineFixture.Submission(draft.Content) with { SourceDraft = draft.Id };
        IOutboxStore outbox = (IOutboxStore)runtime.Catalog;
        Assert.That(await outbox.QueueAsync(submission, CancellationToken.None), Is.True);
        Assert.That(await runtime.Application.DeleteDraftAsync(draft, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));

        // Assert
        Assert.That(await store.ReadAsync(draft.Id, draft.Account, CancellationToken.None), Is.EqualTo(draft));
        OutboxEntry preserved = (await outbox.ReadAsync(submission.Id, draft.Account, CancellationToken.None))!;
        Assert.That(preserved.State, Is.EqualTo(SubmissionState.Queued));
        Assert.That(preserved.Submission.Mime, Is.EqualTo(submission.Mime));
        Assert.That(preserved.Submission.SourceDraft, Is.EqualTo(draft.Id));
        Assert.That(preserved.Submission.Recipients, Is.EqualTo(submission.Recipients));
        Assert.That(provider.DraftDeletes, Is.Zero);
    }

    [Test]
    public async Task DeleteDraftAsync_WhenSubmissionWasCancelled_RemovesDraftWithoutDeletingFrozenMailEvidence()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot draft = await SaveAsync(runtime, "Cancelled source", false);
        IOutboxStore outbox = (IOutboxStore)runtime.Catalog;
        FrozenSubmission submission = EngineFixture.Submission(draft.Content) with { SourceDraft = draft.Id };
        await outbox.QueueAsync(submission, CancellationToken.None);
        OutboxEntry queued = (await outbox.ReadAsync(submission.Id, draft.Account, CancellationToken.None))!;
        Assert.That(await outbox.CancelQueuedAsync(submission.Id, draft.Account, queued.Revision, CancellationToken.None), Is.True);

        // Act
        Failure result = await runtime.Application.DeleteDraftAsync(draft, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(await ((IDraftStore)runtime.Catalog).ReadAsync(draft.Id, draft.Account, CancellationToken.None), Is.Null);
        OutboxEntry cancelled = (await outbox.ReadAsync(submission.Id, draft.Account, CancellationToken.None))!;
        Assert.That(cancelled.State, Is.EqualTo(SubmissionState.Cancelled));
        Assert.That(cancelled.Submission.Mime, Is.EqualTo(submission.Mime));
        Assert.That(cancelled.Submission.Recipients, Is.EqualTo(submission.Recipients));
    }

    [TestCase("ordinary", Failure.None)]
    [TestCase("generation", Failure.NeedsReview)]
    [TestCase("without-uidplus", Failure.Unsupported)]
    public async Task DeleteDraftAsync_WhenImapIdentityIsSelected_UsesOnlySafeUidScopedDeletion(
        string variation,
        Failure expected
    )
    {
        // Arrange
        await using LocalMailServer server = new LocalMailServer(true)

        {
            AdvancedImap = variation != "without-uidplus"
        };
        InMemoryCredentialStore credentials = new InMemoryCredentialStore();
        await credentials.WriteAsync("draft-delete", new Credential("synthetic", false), CancellationToken.None);
        MailEndpoint endpoint = new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "draft-delete");
        await using ImapAdapter adapter = new ImapAdapter(EngineFixture.Account, endpoint, credentials, 1048576)

        {
            FixtureAuthority = server.Authority
        };
        await using Postfold.Storage.Stores.SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await adapter.DiscoverAsync(CancellationToken.None);
        IRemoteDraftDeleter deleter = (IRemoteDraftDeleter)adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!;
        Postfold.Core.Models.Messages.ProviderIdentity identity = EngineFixture.Message(1, variation == "generation" ? 6u : 7u).Provider;

        // Act
        Failure result = await deleter.DeleteDraftAsync(identity, CancellationToken.None);
        if (variation == "ordinary")
        {
            Assert.That(await deleter.DeleteDraftAsync(identity, CancellationToken.None), Is.EqualTo(Failure.None));
        }

        // Assert
        Assert.That(result, Is.EqualTo(expected));
        Assert.That(server.Commands.Any(item => item.Contains(" UID EXPUNGE 1", StringComparison.Ordinal)), Is.EqualTo(variation == "ordinary"));
        Assert.That(server.Commands.Any(item => item.EndsWith(" EXPUNGE", StringComparison.Ordinal)), Is.False);
        Assert.That(server.Commands.Any(item => item.Contains(" STORE ", StringComparison.Ordinal)), Is.EqualTo(variation == "ordinary"));
    }

    [Test]
    public async Task DeleteDraftAsync_WhenCreationIdentityIsUncertain_KeepsTheDraftForReconciliation()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot draft = await SaveAsync(runtime, "Unknown server identity", false);
        provider.LoseDraftAcknowledgement = true;
        Assert.That(await runtime.Application.PushDraftAsync(draft.Account, draft.Id, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        DraftSnapshot held = (await runtime.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Single();

        // Act
        Failure result = await runtime.Application.DeleteDraftAsync(held, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That((await runtime.Catalog.DraftsAsync(draft.Account, CancellationToken.None)).Single(), Is.EqualTo(held));
        Assert.That(provider.DraftDeletes, Is.Zero);
    }

    private static async Task<DraftSnapshot> SaveAsync(
        DesktopMailRuntime runtime,
        string subject,
        bool remote
    )
    {
        Guid id = Guid.NewGuid();
        await runtime.Application.SaveDraftAsync(EngineFixture.Account, id, Content(subject), CancellationToken.None);
        if (remote)
        {
            Assert.That(await runtime.Application.PushDraftAsync(EngineFixture.Account, id, CancellationToken.None), Is.EqualTo(Failure.None));
        }
        return (await runtime.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
    }

    private static ComposeContent Content(
        string subject
    )
    {
        return new ComposeContent(GmailFixture.Binding.Address, ["recipient@synthetic.invalid"], subject, "Draft text", DateTimeOffset.UnixEpoch, "delete@synthetic.invalid");
    }
}

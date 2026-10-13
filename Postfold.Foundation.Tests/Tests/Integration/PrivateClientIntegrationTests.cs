using Avalonia.Headless.NUnit;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Desktop;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Integration;

public sealed class PrivateClientIntegrationTests
{
    [AvaloniaTest]
    public async Task CancelQueuedAsync_WhenClientCancelsAndRestarts_PreservesFrozenContentAndSettings()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        EngineLimits limits = new EngineLimits(2, 16, 1, 10, 1048576, 50, 0, 0, 10485760);
        AccountId account = new AccountId(1);
        ComposeContent content = new ComposeContent(
            "sender@example.invalid",
            ["recipient@example.invalid"],
            "Cancelled content",
            "Preserve my edits",
            DateTimeOffset.UnixEpoch,
            "cancelled@example.invalid"
        );
        AppearanceSettings appearance = AppearanceSettings.Default with
        {
            Preview = "Bottom",
            Density = "Compact",
            FontSize = 20
        };
        Guid submission;
        await using (DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!)
        {
            await host.SeedSyntheticAsync(6, CancellationToken.None);
            await host.RestoreAccountsAsync(new SessionCredentialStore(), CancellationToken.None);
            Guid draft = Guid.NewGuid();
            await host.Application.SaveDraftAsync(account, draft, content, CancellationToken.None);
            FrozenSubmission frozen = (await host.Application.SendAsync(account, content, draft, null, CancellationToken.None)).Value!;
            submission = frozen.Id;
            OutboxEntry queued = (await host.Catalog.OutboxAsync(account, CancellationToken.None))[0];

            // Act
            Failure cancelled = await host.Application.CancelQueuedAsync(queued, CancellationToken.None);
            Failure stale = await host.Application.CancelQueuedAsync(queued, CancellationToken.None);
            await host.Appearance.SaveAsync(appearance, CancellationToken.None);

            // Assert
            Assert.That(cancelled, Is.EqualTo(Failure.None));
            Assert.That(stale, Is.EqualTo(Failure.NeedsReview));
            Assert.That(await host.Application.ProcessOutboxAsync(account, CancellationToken.None), Is.EqualTo(Failure.Unavailable));
        }

        await using DesktopMailRuntime reopened = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;
        OutboxEntry preserved = (await reopened.Catalog.OutboxAsync(account, CancellationToken.None))[0];
        Outcome<ComposeContent> editable = await reopened.Application.PrepareResendAsync(preserved, CancellationToken.None);
        Assert.That(preserved.Submission.Id, Is.EqualTo(submission));
        Assert.That(preserved.State, Is.EqualTo(SubmissionState.Cancelled));
        Assert.That(editable.Value!.PlainText, Is.EqualTo(content.PlainText + "\r\n"));
        Assert.That(editable.Value.Predecessor, Is.EqualTo(submission));
        Guid replacementDraft = Guid.NewGuid();
        await reopened.RestoreAccountsAsync(new SessionCredentialStore(), CancellationToken.None);
        IMailAccountEngine originalEngine = reopened.Find(account)!;
        SessionCredentialStore renewed = new SessionCredentialStore();
        Assert.That(await reopened.ReplaceCredentialsAsync(account, renewed, CancellationToken.None), Is.EqualTo(Failure.None));
        Assert.That(reopened.Find(account), Is.Not.SameAs(originalEngine));
        Assert.That(
            (await reopened.Catalog.OutboxAsync(account, CancellationToken.None))[0].Submission.Id,
            Is.EqualTo(submission)
        );
        Assert.That(
            (await reopened.Catalog.OutboxAsync(account, CancellationToken.None))[0].State,
            Is.EqualTo(SubmissionState.Cancelled)
        );
        DraftSnapshot replacement = (await reopened.Application.SaveDraftAsync(
            account,
            replacementDraft,
            editable.Value,
            CancellationToken.None
        )).Value!;
        ComposeContent restored = (await reopened.Application.ReadDraftAsync(replacement, CancellationToken.None)).Value!;
        Assert.That(restored.Predecessor, Is.EqualTo(submission));
        Assert.That(await reopened.Appearance.ReadAsync(CancellationToken.None), Is.EqualTo(appearance));
    }

    [AvaloniaTest]
    public async Task Client_WhenOfflineDraftAndMutationAreSaved_ReopensDurablyWithoutNetwork()
    {
        // Arrange
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Postfold.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException();
        }

        string directory = Path.Combine(root, "artifacts", "client-tests", Guid.NewGuid().ToString("N"));
        EngineLimits limits = new EngineLimits(2, 16, 1, 10, 1048576, 50, 0, 0, 10485760);
        Guid draftId = Guid.NewGuid();
        AccountId account = new AccountId(1);
        ComposeContent draft = new ComposeContent(
            "sender@example.invalid",
            [],
            "Offline draft",
            "Preserved text",
            DateTimeOffset.UnixEpoch,
            "synthetic@example.invalid"
        );
        MessageId changed;
        await using (DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!)
        {
            await host.SeedSyntheticAsync(360, CancellationToken.None);
            await host.RestoreAccountsAsync(new SessionCredentialStore(), CancellationToken.None);
            MainWindowViewModel model = new MainWindowViewModel(host.Application);
            MainWindow window = new MainWindow
            {
                DataContext = model
            };
            window.Configure(host.Application, null);
            window.Show();
            try
            {
                await model.InitialiseAsync();
                MessageSnapshot selected = model.Messages[0];
                account = selected.Account;
                model.Selected = selected;
                await model.ReadSelectedAsync(false);
                Assert.That(model.Body, Does.Contain("synthetic cached mail"));

                // Act
                Outcome<DraftSnapshot> saved = await host.Application.SaveDraftAsync(account, draftId, draft, CancellationToken.None);
                Failure mutation = await host.Application.MutateAsync(
                    selected,
                    MutationKind.SetFlagged,
                    true,
                    null,
                    CancellationToken.None
                );
                changed = selected.Id;

                // Assert
                Assert.That(saved.IsSuccess, Is.True);
                Assert.That(mutation, Is.EqualTo(Failure.None));
                Assert.That(await host.Application.SynchroniseAsync(account, CancellationToken.None), Is.EqualTo(Failure.Unavailable));
            }
            finally
            {
                await model.DisposeAsync();
                window.Close();
            }
        }

        await using DesktopMailRuntime reopened = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;
        DraftSnapshot stored = (await reopened.Catalog.DraftsAsync(account, CancellationToken.None))[0];
        Outcome<ComposeContent> content = await reopened.Application.ReadDraftAsync(stored, CancellationToken.None);
        Outcome<SearchPage> page = await reopened.Query.SearchAsync(
            new SearchRequest(account, false, "", null, false, 100),
            CancellationToken.None
        );
        MessageSnapshot pending = System.Linq.Enumerable.Single(page.Value!.Messages, item => item.Id == changed);
        Assert.That(content.Value!.PlainText, Is.EqualTo(draft.PlainText));
        Assert.That(stored.Id, Is.EqualTo(draftId));
        Assert.That(pending.Flagged, Is.True);
        Assert.That(pending.Pending, Is.EqualTo(PendingState.Queued));
    }
}

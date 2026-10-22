using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Services;
using Postfold.Desktop.Services;
using Postfold.UI.Policies;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class ClientTests
{
    [AvaloniaTest]
    public async Task InitialiseAsync_WhenCatalogIsUnchanged_RetainsNavigationAndRefreshesMessages()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        NavigationItemViewModel[] navigation = model.Navigation.ToArray();
        MessageSnapshot message = Message(2, "Newly synchronised message");
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<SearchPage>.Success(new SearchPage([message], null)));

        // Act
        await model.InitialiseAsync();

        // Assert
        Assert.That(model.Messages, Is.EqualTo(new[] { message }));
        Assert.That(model.Navigation.Count, Is.EqualTo(navigation.Length));
        for (int index = 0; index < navigation.Length; index++)
        {
            Assert.That(model.Navigation[index], Is.SameAs(navigation[index]));
        }
    }

    [AvaloniaTest]
    public async Task AttachLoadedModel_WhenNavigationEchoesItsSelection_RetainsThePageAndExplicitRefreshStillQueries()
    {
        // Arrange
        IMailApplication application = Application();
        MessageSnapshot message = Message(1, "Prepared inbox");
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<SearchPage>.Success(new SearchPage([message], null)));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        application.ClearReceivedCalls();
        MainWindow main = new MainWindow();
        main.Configure(application, null);
        main.Show();
        try
        {
            // Act
            main.DataContext = model;
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.Messages, Is.EqualTo(new[] { message }));
            await application.DidNotReceive().SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>());
            await model.RefreshAsync(false);
            await application.Received(1).SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task RenameAccount_WhenSavedFromSidebar_RefreshesLabelWithoutChangingEmailOrFolderSelection()
    {
        // Arrange
        IMailApplication application = Application();
        AccountConfiguration original = (await application.Catalog.AccountsAsync(CancellationToken.None))[0];
        IAccountManagement management = Substitute.For<IAccountManagement>();
        management.RenameAsync(original.Id, "Personal", Arg.Any<CancellationToken>()).Returns(call =>
        {
            application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(original with
            {
                Name = "Personal"
            }));
            return Failure.None;
        });
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null, management: management);
        main.Show();
        try
        {
            await model.InitialiseAsync();
            model.Navigation.First(item => item.Scope.IsAccount).IsExpanded = false;

            // Act
            main.OpenSettings("Accounts", original.Id);
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RenameAccount")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            Window dialog = main.OwnedWindows.Single(window => window is not SettingsWindow);
            dialog.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "RenameName").Text = "Personal";
            dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveAccountName").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            settings.Close();

            // Assert
            Assert.That(model.Accounts[0].Name, Is.EqualTo("Personal"));
            Assert.That(model.Accounts[0].Incoming, Is.EqualTo(original.Incoming));
            Assert.That(model.Accounts[0].Outgoing, Is.EqualTo(original.Outgoing));
            Assert.That(model.Navigation.First(item => item.Scope.IsAccount).IsExpanded, Is.False);
            Assert.That(main.OwnedWindows, Is.Empty);
            await management.Received(1).RenameAsync(original.Id, "Personal", Arg.Any<CancellationToken>());
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task SynchroniseCommand_WhenRefreshIsRequested_SyncsSelectedAccountOrAllAccounts(bool unified)
    {
        // Arrange
        IMailApplication application = Application();
        AccountConfiguration first = (await application.Catalog.AccountsAsync(CancellationToken.None))[0];
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(first, first with
        {
            Id = new AccountId(2)
        }));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Scope = unified ? model.Scopes.First(scope => scope.Account is null && scope.InboxOnly) :
            model.Scopes.First(scope => scope.Account == new AccountId(2));

        // Act
        await model.SynchroniseCommand.ExecuteAsync(null);

        // Assert
        await application.Received(1).SynchroniseAsync(new AccountId(2), Arg.Any<CancellationToken>());
        await application.Received(unified ? 1 : 0).SynchroniseAsync(new AccountId(1), Arg.Any<CancellationToken>());
        Assert.That(model.Status, Is.Empty);
    }

    [AvaloniaTest]
    public async Task Navigation_WhenAccountIsCollapsed_PreservesFolderSelectionAndExpansionAcrossRefresh()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, Substitute.For<IAccountSetup>());
        main.Show();
        try
        {
            await model.InitialiseAsync();
            main.UpdateLayout();
            NavigationItemViewModel account = model.Navigation.First(item => item.Scope.IsAccount);
            Postfold.UI.Models.MailScope? selected = model.Scope;
            TreeView tree = main.FindControl<TreeView>("ScopeList")!;
            TreeViewItem container = (TreeViewItem)tree.ContainerFromItem(account)!;

            // Act
            container.IsExpanded = false;
            await model.InitialiseAsync();
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.Navigation.First(item => item.Scope.Account == account.Scope.Account).IsExpanded, Is.False);
            Assert.That(model.Scope, Is.EqualTo(selected));
            Assert.That(model.NavigationSelection!.Scope, Is.EqualTo(selected));
            Assert.That(model.Status, Is.Empty);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task InitialiseAsync_WhenNoAccountsExist_PromptsSetupAndDisablesCompose()
    {
        // Arrange
        IMailApplication application = Application();
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, Substitute.For<IAccountSetup>());
        main.Show();
        try
        {
            // Act
            await model.InitialiseAsync();
            main.OpenAccountSetupIfNeeded();

            // Assert
            Assert.That(model.HasAccounts, Is.False);
            Assert.That(main.FindControl<Button>("ComposeButton")!.IsEnabled, Is.False);
            Assert.That(main.FindControl<Button>("SetupButton")!.IsEnabled, Is.True);
            Assert.That(model.Status, Is.Empty);
            Assert.That(main.OwnedWindows.OfType<AccountSetupWindow>().Count(), Is.EqualTo(1));
            main.OpenAccountSetupIfNeeded();
            Assert.That(main.OwnedWindows.OfType<AccountSetupWindow>().Count(), Is.EqualTo(1));
            main.OwnedWindows.OfType<AccountSetupWindow>().Single().Close();
            await main.WaitForActionsAsync();
            Assert.That(main.OwnedWindows, Is.Empty);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task FlushComposeAsync_WhenOutboxSubmissionIsActive_CancelsAndObservesWorkBeforeShutdown()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        FrozenSubmission frozen = new FrozenSubmission(
            Guid.NewGuid(),
            account,
            new BlobReference(Guid.NewGuid(), account, 10, "synthetic", true),
            "sender@synthetic.invalid",
            ["recipient@synthetic.invalid"]
        );
        application.Catalog.CommandsAsync(account, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<Postfold.Core.Models.Operations.CommandSnapshot>.Empty);
        application.Catalog.OutboxAsync(account, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create(new OutboxEntry(frozen, SubmissionState.Queued, 1, null)));
        TaskCompletionSource<Failure> pending = new TaskCompletionSource<Failure>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        CancellationToken submission = default;
        application.SubmitAsync(account, frozen.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            submission = call.Arg<CancellationToken>();
            return pending.Task;
        });
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null);
        main.Show();
        try
        {
            await main.CommandsAsync(account);
            Window outbox = main.OwnedWindows.Single();
            ListBox sends = outbox.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "SendIssues");
            sends.SelectedIndex = 0;
            outbox.UpdateLayout();
            Button transmit = outbox.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Send");
            transmit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Act
            Task<bool> shutdown = main.FlushComposeAsync();

            // Assert
            Assert.That(submission.IsCancellationRequested, Is.True);
            Assert.That(shutdown.IsCompleted, Is.False);
            pending.SetResult(Failure.NeedsReview);
            Assert.That(await shutdown, Is.True);
        }
        finally
        {
            pending.TrySetResult(Failure.NeedsReview);
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("Reply", true)]
    [TestCase("ReplyAll", true)]
    [TestCase("Forward", true)]
    [TestCase("Reply", false)]
    [TestCase("ReplyAll", false)]
    [TestCase("Forward", false)]
    public async Task OpenCompose_WhenReplyOrForwardNeedsOriginalContent_UsesParsedReplyTargetAndNeverQuotesMissingState(
        string kind,
        bool missing
    )
    {
        // Arrange
        IMailApplication application = Application();
        application.ReadAsync(Arg.Any<MessageSnapshot>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(missing ?
            Outcome<MimeSnapshot>.Fail(Failure.NotFound) : Outcome<MimeSnapshot>.Success(
                new MimeSnapshot(
    "Original",
    "Displayed sender",
    null,
    "Original text",
    null,
    [],
    ["reply@synthetic.invalid"],
    ["test@example.invalid", "copy@synthetic.invalid", "reply@synthetic.invalid"],
    ["copy@synthetic.invalid"]
)
            ));
        ComposeContent? saved = null;
        application.SaveDraftAsync(
            Arg.Any<AccountId>(),
            Arg.Any<Guid>(),
            Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()
        ).Returns(call =>
            {
                saved = call.Arg<ComposeContent>();
                return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
            });
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "Original");
        await model.ReadSelectedAsync(false);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null);
        main.Show();
        try
        {
            // Act
            main.OpenCompose(kind);
            ComposeWindow? compose = main.OwnedWindows.OfType<ComposeWindow>().SingleOrDefault();
            if (compose is not null)
            {
                await compose.FlushAsync();
                compose.ConfirmShutdown();
                compose.Close();
            }

            // Assert
            Assert.That(compose is null, Is.EqualTo(missing));
            if (!missing)
            {
                Assert.That(saved!.PlainText, Does.Contain("> Original text"));
                Assert.That(saved.Recipients, Is.EqualTo(kind == "Reply" ? new[] { "reply@synthetic.invalid" } :
                    kind == "ReplyAll" ? new[] { "reply@synthetic.invalid", "copy@synthetic.invalid" } : System.Array.Empty<string>()));
            }
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false, false, false)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    public async Task MoveAsync_WhenCachedCapabilitiesVary_EnablesOnlySafeMoves(
        bool nativeMove,
        bool selectiveExpunge,
        bool allowed
    )
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic", "fixture");
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(
    account,
    "Synthetic",
    endpoint,
    endpoint,
    new Capabilities(false, false, false, nativeMove, selectiveExpunge, false)
)
        ));
        MailboxSnapshot source = new MailboxSnapshot(
            account,
            new FolderId(1),
            "INBOX",
            "Inbox",
            MailboxRole.Inbox,
            true,
            true,
            1
        );
        MailboxSnapshot target = source with
        {
            Id = new FolderId(2),
            RemoteKey = "Archive",
            Role = MailboxRole.Archive
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(source, target));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "Movable") with
        {
            Provider = new ProviderIdentity(account, "1", new ImapIdentity(account, "INBOX", 1, 1))
        };

        // Act
        bool canMove = model.CanMove;
        bool canArchive = model.ArchiveCommand.CanExecute(null);
        await model.MoveAsync(target);

        // Assert
        Assert.That(canMove, Is.EqualTo(allowed));
        Assert.That(canArchive, Is.EqualTo(allowed));
        await application.Received(allowed ? 1 : 0).MutateAsync(
            Arg.Any<MessageSnapshot>(),
            MutationKind.Move,
            false,
            "Archive",
            Arg.Any<CancellationToken>()
        );
    }

    [AvaloniaTest]
    public async Task OpenCompose_WhenRestoredWorkNamesAnotherAccount_SavesOnlyToThatAccount()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId target = new AccountId(2);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "other@example.invalid", "fixture");
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(new AccountId(1), "First", endpoint, endpoint),
            new AccountConfiguration(target, "Second", endpoint, endpoint)
        ));
        application.SaveDraftAsync(
            Arg.Any<AccountId>(),
            Arg.Any<Guid>(),
            Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()
        ).Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "First account selection");
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null);
        main.Show();
        main.OpenCompose(activation: new ComposeContent(
            "other@example.invalid",
            [],
            "Restored",
            "Saved body",
            DateTimeOffset.UnixEpoch,
            "restored@example.invalid"
        ), targetAccount: target);
        ComposeWindow compose = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<ComposeWindow>(main.OwnedWindows));
        try
        {
            // Act
            await compose.FlushAsync();

            // Assert
            await application.Received().SaveDraftAsync(
                target,
                Arg.Any<Guid>(),
                Arg.Is<ComposeContent>(content => content.PlainText == "Saved body"),
                Arg.Any<CancellationToken>()
            );
            await application.DidNotReceive().SaveDraftAsync(
                new AccountId(1),
                Arg.Any<Guid>(),
                Arg.Any<ComposeContent>(),
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            compose.ConfirmShutdown();
            compose.Close();
            main.Close();
            await model.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public void ApplyAppearance_WhenPreviewChanges_ReflowsAndKeepsResizableLayout()
    {
        // Arrange
        MainWindow window = new MainWindow();
        window.Show();
        try
        {
            // Act
            window.ApplyAppearance(AppearanceSettings.Default with
            {
                Preview = "Bottom",
                Density = "Compact",
                FontSize = 24
            });

            // Assert
            Assert.That(window.FindControl<Grid>("Panes")!.RowDefinitions, Has.Count.EqualTo(3));
            Assert.That(Grid.GetRow(window.FindControl<DockPanel>("PreviewPane")!), Is.EqualTo(2));
            Assert.That(window.FontSize, Is.EqualTo(24));
            Assert.That(window.FindControl<GridSplitter>("PreviewSplitter")!.IsVisible, Is.True);
            window.ApplyAppearance(AppearanceSettings.Default with
            {
                Preview = "Hidden"
            });
            Assert.That(window.FindControl<DockPanel>("PreviewPane")!.IsVisible, Is.False);
            Assert.That(window.FindControl<GridSplitter>("PreviewSplitter")!.IsVisible, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task RefreshAsync_WhenLoadingMore_AppendsRowsAndRejectsStaleScopeCursor()
    {
        // Arrange
        IMailApplication application = Application();
        MessageSnapshot first = Message(1, "First");
        MessageSnapshot second = Message(2, "Second");
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([first], "next")),
            Outcome<SearchPage>.Success(new SearchPage([second], "third"))
        );
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = first;

        // Act
        await model.RefreshAsync(true);
        model.Terms = "changed";
        await model.RefreshAsync(true);

        // Assert
        Assert.That(model.Messages, Is.EqualTo(new[] { first, second }));
        Assert.That(model.Selected?.Id, Is.EqualTo(first.Id));
        Assert.That(model.MoreCommand.CanExecute(null), Is.False);
        await application.Received(2).SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task MoveAsync_WhenSourceIsReadOnlyOrDestinationIsForeign_DoesNotRecordIntent()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        MailboxSnapshot source = new MailboxSnapshot(
            account,
            new FolderId(1),
            "INBOX",
            "Inbox",
            MailboxRole.Inbox,
            true,
            false,
            1
        );
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(source));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "Read only") with
        {
            Provider = new ProviderIdentity(account, "1", new ImapIdentity(account, "INBOX", 1, 1))
        };

        // Act
        await model.MoveAsync(source with
        {
            Account = new AccountId(2),
            Writable = true
        });

        // Assert
        Assert.That(model.ReadCommand.CanExecute(null), Is.False);
        Assert.That(model.FlagCommand.CanExecute(null), Is.False);
        Assert.That(model.ArchiveCommand.CanExecute(null), Is.False);
        await application.DidNotReceive().MutateAsync(
            Arg.Any<MessageSnapshot>(),
            Arg.Any<MutationKind>(),
            Arg.Any<bool>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    [AvaloniaTest]
    public async Task PrepareShutdownAsync_WhenDraftSaveFails_PreservesComposeWindow()
    {
        // Arrange
        IMailApplication application = Application();
        application.SaveDraftAsync(
            Arg.Any<AccountId>(),
            Arg.Any<Guid>(),
            Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()
        ).Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        ComposeWindow window = new ComposeWindow(
            application,
            new AccountId(1),
            new ComposeContent(
    "sender@example.invalid",
    [],
    "Unsent",
    "Preserve this text",
    DateTimeOffset.UnixEpoch,
    "synthetic@example.invalid"
)
        );
        window.Show();
        try
        {
            // Act
            window.BeginShutdown();
            bool ready = await window.PrepareShutdownAsync();

            // Assert
            Assert.That(ready, Is.False);
            Assert.That(window.IsVisible, Is.True);
            window.ResumeEditing();
            Assert.That(window.IsEnabled, Is.True);
        }
        finally
        {
            window.ConfirmShutdown();
            window.Close();
        }
    }

    [TestCase("mailto:a@example.invalid?subject=Hi&body=Hello%0Athere", true)]
    [TestCase("mailto:a@example.invalid?subject=Hi%0ABcc%3Ab@example.invalid", false)]
    [TestCase("mailto:a@example.invalid?attach=file:///secret", false)]
    [TestCase("mailto:a@example.invalid?body=%ZZ", false)]
    [TestCase("mailto:a@example.invalid?subject=a&subject=b", false)]
    [TestCase("https://example.invalid", false)]
    public void Parse_WhenActivationIsUntrusted_ValidatesWithoutSending(string activation, bool expected)
    {
        // Arrange
        string input = activation;

        // Act
        Outcome<ComposeContent> result = MailtoActivation.Parse(input);

        // Assert
        Assert.That(result.IsSuccess, Is.EqualTo(expected));
    }

    [TestCase("127.0.0.1")]
    [TestCase("10.0.0.1")]
    [TestCase("100.64.0.1")]
    [TestCase("192.168.1.1")]
    [TestCase("169.254.169.254")]
    [TestCase("198.18.0.1")]
    [TestCase("::1")]
    [TestCase("::ffff:127.0.0.1")]
    public void IsPublic_WhenDestinationIsLocalOrReserved_Denies(string value)
    {
        // Arrange
        IPAddress address = IPAddress.Parse(value);

        // Act
        bool allowed = RemoteImageFetcher.IsPublic(address);

        // Assert
        Assert.That(allowed, Is.False);
    }

    [Test]
    public async Task FetchAsync_WhenConsentIsAbsent_DoesNotContactDestination()
    {
        // Arrange
        RemoteImageFetcher fetcher = new RemoteImageFetcher();

        // Act
        Outcome<byte[]> result = await fetcher.FetchAsync(new Uri("https://8.8.8.8/image.png"), false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [Test]
    public void Sanitise_WhenHtmlContainsActiveAndRemoteContent_KeepsOnlyPassiveText()
    {
        // Arrange
        string input = "<script>alert(1)</script><img src='https://example.invalid/pixel'><iframe src='file:///x'></iframe>" +
            "<form action='https://example.invalid'><input></form><p onclick='x()' style='background:url(file:///x)'>Safe</p>";

        // Act
        Outcome<string> result = HtmlPolicy.Sanitise(input, 4096);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value, Does.Contain("<p>Safe</p>"));
        Assert.That(result.Value, Does.Not.Contain("https://example.invalid"));
        Assert.That(result.Value, Does.Not.Contain("onclick"));
        Assert.That(result.Value, Does.Not.Contain("<script"));
        Assert.That(result.Value, Does.Contain(HtmlPolicy.Csp));
    }

    [AvaloniaTest]
    public async Task RefreshAsync_WhenSnapshotsRefresh_PreservesAccountScopedSelection()
    {
        // Arrange
        IMailApplication application = Application();
        MessageSnapshot message = Message(1, "First");
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(Outcome<SearchPage>.Success(new SearchPage([message], null)))
        );
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow window = new MainWindow
        {
            DataContext = model
        };
        window.Show();
        try
        {
            await model.InitialiseAsync();
            model.Selected = message;
            application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult(Outcome<SearchPage>.Success(new SearchPage([message with { Subject = "Refreshed" }], null)))
            );

            // Act
            await model.RefreshAsync(false);

            // Assert
            Assert.That(model.Selected?.Id, Is.EqualTo(message.Id));
            Assert.That(model.Selected?.Subject, Is.EqualTo("Refreshed"));
            Assert.That(window.FindControl<ListBox>("MessageList")!.ItemCount, Is.EqualTo(1));
        }
        finally
        {
            await model.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ReadSelectedAsync_WhenOldReadCompletesLate_DoesNotReplaceCurrentMessage()
    {
        // Arrange
        IMailApplication application = Application();
        MessageSnapshot first = Message(1, "First");
        MessageSnapshot second = Message(2, "Second");
        TaskCompletionSource<Outcome<MimeSnapshot>> late = new TaskCompletionSource<Outcome<MimeSnapshot>>();
        application.ReadAsync(first, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(late.Task);
        application.ReadAsync(second, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Success(new MimeSnapshot("Second", "", null, "current", null, []))
        );
        MainWindowViewModel model = new MainWindowViewModel(application);
        model.Selected = first;
        model.Selected = second;

        // Act
        late.SetResult(Outcome<MimeSnapshot>.Success(new MimeSnapshot("First", "", null, "stale", null, [])));
        await model.ReadSelectedAsync(false);

        // Assert
        Assert.That(model.Body, Is.EqualTo("current"));
        await model.DisposeAsync();
    }

    [AvaloniaTest]
    public async Task RefreshAsync_WhenFiltersChange_InvalidatesContinuationAndPreservesStableSelection()
    {
        // Arrange
        IMailApplication application = Application();
        MessageSnapshot message = Message(1, "First");
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([message], "next"))
        );
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = message;
        Postfold.UI.Models.MessageSummary row = model.Rows.Single();

        // Act
        await model.RefreshAsync(false);
        model.UnreadOnly = true;
        bool canContinueOldFilter = model.MoreCommand.CanExecute(null);
        await model.RefreshAsync(false);

        // Assert
        Assert.That(model.Selected, Is.EqualTo(message));
        Assert.That(model.Rows.Single(), Is.SameAs(row));
        Assert.That(canContinueOldFilter, Is.False);
        await application.Received().SearchAsync(
            Arg.Is<SearchRequest>(request => request.UnreadOnly && request.Continuation == null),
            Arg.Any<CancellationToken>()
        );
    }

    [AvaloniaTest]
    [TestCase("UnreadFilter", true)]
    [TestCase("AllFilterButton", false)]
    public async Task Filter_WhenControlChanges_SubmitsCurrentFilterToTheApplication(
        string control,
        bool unread
    )
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null);
        main.Show();
        try
        {
            application.ClearReceivedCalls();

            // Act
            main.FindControl<RadioButton>("UnreadFilter")!.IsChecked = !unread;
            await main.WaitForActionsAsync();
            application.ClearReceivedCalls();
            main.FindControl<RadioButton>(control)!.IsChecked = true;
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.UnreadOnly, Is.EqualTo(unread));
            await application.Received().SearchAsync(
                Arg.Is<SearchRequest>(request => request.UnreadOnly == unread),
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task ReadSelectedAsync_WhenRecipientHeaderIsBounded_DisablesReplyAllButPreservesReadingAndReply()
    {
        // Arrange
        IMailApplication application = Application();
        application.ReadAsync(Arg.Any<MessageSnapshot>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Success(new MimeSnapshot(
                "Subject",
                "Sender",
                null,
                "Readable",
                null,
                [],
                ["sender@synthetic.invalid"],
                ["visible@synthetic.invalid"],
                [],
                false
            ))
        );
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        model.Selected = Message(1, "Bounded header");

        // Act
        await model.ReadSelectedAsync(false);

        // Assert
        Assert.That(model.Body, Is.EqualTo("Readable"));
        Assert.That(model.CanComposeFromMessage, Is.True);
        Assert.That(model.CanReplyAll, Is.False);
        Assert.That(model.ReaderRecipients, Does.Contain(Strings.Get("RecipientsLimited")));
    }

    [AvaloniaTest]
    public async Task Scope_WhenControlSelectsAnotherAccount_QueriesOnlyThatAccount()
    {
        // Arrange
        IMailApplication application = Application();
        ImmutableArray<AccountConfiguration> accounts = await application.Catalog.AccountsAsync(CancellationToken.None);
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(accounts.Add(accounts[0] with
        {
            Id = new AccountId(2),
            Name = "Second synthetic account"
        }));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null);
        main.Show();
        try
        {
            application.ClearReceivedCalls();

            // Act
            main.FindControl<TreeView>("ScopeList")!.SelectedItem = model.Navigation.First(item => item.Scope.Account == new AccountId(2));
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.Scope!.Account, Is.EqualTo(new AccountId(2)));
            await application.Received().SearchAsync(
                Arg.Is<SearchRequest>(request => request.Account == new AccountId(2) && !request.Unified),
                Arg.Any<CancellationToken>()
            );
            await application.DidNotReceive().SearchAsync(
                Arg.Is<SearchRequest>(request => request.Account == new AccountId(1)),
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public void RegroupMessages_WhenLocalMidnightChanges_UpdatesHeadingsWithoutLoadingBodies()
    {
        // Arrange
        DesignClock clock = new DesignClock();
        IMailApplication application = Application();
        MainWindowViewModel model = new MainWindowViewModel(application, clock);
        model.Messages.Add(Message(1, "Today") with
        {
            Date = clock.GetUtcNow()
        });
        model.Messages.Add(Message(2, "Same date") with
        {
            Date = clock.GetUtcNow().AddHours(-1)
        });
        model.RegroupMessages();
        string firstHeading = model.Rows[0].Group;

        // Act
        clock.Now = clock.Now.AddDays(1);
        model.RegroupMessages();

        // Assert
        Assert.That(firstHeading, Is.EqualTo(Strings.Today));
        Assert.That(model.Rows[0].Group, Is.EqualTo(Strings.Yesterday));
        Assert.That(model.Rows[1].HasGroup, Is.False);
        application.DidNotReceiveWithAnyArgs().ReadAsync(default!, default, default);
    }

    private sealed class DesignClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "test@example.invalid", "synthetic");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new AccountConfiguration(new AccountId(1), "Synthetic", endpoint, endpoint))
        );
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxSnapshot>.Empty);
        application.ReadAsync(Arg.Any<MessageSnapshot>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Fail(Failure.NotFound)
        );
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([], null))
        );
        return application;
    }

    private static MessageSnapshot Message(long id, string subject)
    {
        AccountId account = new AccountId(1);
        return new MessageSnapshot(
            account,
            new MessageId(id),
            new ProviderIdentity(account, id.ToString(), null),
            subject,
            "fixture@example.invalid",
            null,
            DateTimeOffset.UnixEpoch,
            100,
            false,
            false,
            ContentAvailability.Missing,
            PendingState.None
        );
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;
using Postfold.UI;

namespace Postfold.Foundation.Tests;

public sealed class ClientTests
{
    [AvaloniaTest]
    [TestCase("Reply", true)]
    [TestCase("Forward", true)]
    [TestCase("Reply", false)]
    [TestCase("Forward", false)]
    public async Task OpenCompose_WhenReplyOrForwardNeedsOriginalContent_UsesParsedReplyTargetAndNeverQuotesMissingState(
        string kind, bool missing)
    {
        // Arrange
        IMailApplication application = Application();
        application.ReadAsync(Arg.Any<MessageSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(missing ?
            Outcome<MimeSnapshot>.Fail(Failure.NotFound) : Outcome<MimeSnapshot>.Success(
                new MimeSnapshot("Original", "Displayed sender", null, "Original text", null, [],
                    ["reply@synthetic.invalid"])));
        ComposeContent? saved = null;
        application.SaveDraftAsync(Arg.Any<AccountId>(), Arg.Any<Guid>(), Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()).Returns(call =>
            {
                saved = call.Arg<ComposeContent>();
                return Outcome<DraftSnapshot>.Fail(Failure.Unavailable);
            });
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "Original");
        await model.ReadSelectedAsync(false);
        MainWindow main = new MainWindow { DataContext = model };
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
                    System.Array.Empty<string>()));
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
    public async Task MoveAsync_WhenCachedCapabilitiesVary_EnablesOnlySafeMoves(bool nativeMove, bool selectiveExpunge,
        bool allowed)
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic", "fixture");
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(account, "Synthetic", endpoint, endpoint,
                new Capabilities(false, false, false, nativeMove, selectiveExpunge, false))));
        MailboxSnapshot source = new MailboxSnapshot(account, new FolderId(1), "INBOX", "Inbox", MailboxRole.Inbox,
            true, true, 1);
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
        await application.Received(allowed ? 1 : 0).MutateAsync(Arg.Any<MessageSnapshot>(), MutationKind.Move,
            false, "Archive", Arg.Any<CancellationToken>());
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
            new AccountConfiguration(target, "Second", endpoint, endpoint)));
        application.SaveDraftAsync(Arg.Any<AccountId>(), Arg.Any<Guid>(), Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()).Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Selected = Message(1, "First account selection");
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        main.OpenCompose(activation: new ComposeContent("other@example.invalid", [], "Restored", "Saved body",
            DateTimeOffset.UnixEpoch, "restored@example.invalid", Signature: "Saved signature"), targetAccount: target);
        ComposeWindow compose = System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<ComposeWindow>(main.OwnedWindows));
        try
        {
            // Act
            await compose.FlushAsync();

            // Assert
            await application.Received().SaveDraftAsync(target, Arg.Any<Guid>(),
                Arg.Is<ComposeContent>(content => content.PlainText == "Saved body" && content.Signature == "Saved signature"),
                Arg.Any<CancellationToken>());
            await application.DidNotReceive().SaveDraftAsync(new AccountId(1), Arg.Any<Guid>(),
                Arg.Any<ComposeContent>(), Arg.Any<CancellationToken>());
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
            Outcome<SearchPage>.Success(new SearchPage([second], "third")));
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
        MailboxSnapshot source = new MailboxSnapshot(account, new FolderId(1), "INBOX", "Inbox",
            MailboxRole.Inbox, true, false, 1);
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
        await application.DidNotReceive().MutateAsync(Arg.Any<MessageSnapshot>(), Arg.Any<MutationKind>(),
            Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task PrepareShutdownAsync_WhenDraftSaveFails_PreservesComposeWindow()
    {
        // Arrange
        IMailApplication application = Application();
        application.SaveDraftAsync(Arg.Any<AccountId>(), Arg.Any<Guid>(), Arg.Any<ComposeContent>(),
            Arg.Any<CancellationToken>()).Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        ComposeWindow window = new ComposeWindow(application, new AccountId(1),
            new ComposeContent("sender@example.invalid", [], "Unsent", "Preserve this text",
                DateTimeOffset.UnixEpoch, "synthetic@example.invalid"));
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
            Task.FromResult(Outcome<SearchPage>.Success(new SearchPage([message], null))));
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow window = new MainWindow { DataContext = model };
        window.Show();
        try
        {
            await model.InitialiseAsync();
            model.Selected = message;
            application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromResult(Outcome<SearchPage>.Success(new SearchPage([message with { Subject = "Refreshed" }], null))));

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
        application.ReadAsync(first, false, Arg.Any<CancellationToken>()).Returns(late.Task);
        application.ReadAsync(second, false, Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Success(new MimeSnapshot("Second", "", null, "current", null, [])));
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

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "test@example.invalid", "synthetic");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new AccountConfiguration(new AccountId(1), "Synthetic", endpoint, endpoint)));
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxSnapshot>.Empty);
        application.ReadAsync(Arg.Any<MessageSnapshot>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<MimeSnapshot>.Fail(Failure.NotFound));
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([], null)));
        return application;
    }

    private static MessageSnapshot Message(long id, string subject)
    {
        AccountId account = new AccountId(1);
        return new MessageSnapshot(account, new MessageId(id), new ProviderIdentity(account, id.ToString(), null),
            subject, "fixture@example.invalid", null, DateTimeOffset.UnixEpoch, 100, false, false,
            ContentAvailability.Missing, PendingState.None);
    }
}

using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class MessageInteractionTests
{
    [AvaloniaTest]
    public async Task InitialiseAsync_WhenBackgroundRefreshRuns_PreservesLoadedPagesSelectionAndCachedBody()
    {
        // Arrange
        IMailApplication application = Application();
        ImmutableArray<MessageSnapshot> first = Enumerable.Range(1, 100).Select(id => Message() with
        {
            Id = new MessageId(id),
            Read = true
        }).ToImmutableArray();
        ImmutableArray<MessageSnapshot> second = Enumerable.Range(101, 100).Select(id => Message() with
        {
            Id = new MessageId(id),
            Read = true
        }).ToImmutableArray();
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            Outcome<SearchPage>.Success(call.Arg<SearchRequest>().Continuation is null ?
                new SearchPage(first, "second") : new SearchPage(second, null)));
        application.ReadAsync(Arg.Any<MessageSnapshot>(), true, Arg.Any<CancellationToken>())
            .Returns(Outcome<MimeSnapshot>.Success(new MimeSnapshot("Synthetic", "sender", null, "Cached body", null, [])));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        await model.RefreshAsync(true);
        await model.OpenMessageAsync(second[20]);

        // Act
        await model.InitialiseAsync(preserveLoadedRows: true);

        // Assert
        Assert.That(model.Messages, Has.Count.EqualTo(200));
        Assert.That(model.Selected, Is.EqualTo(second[20]));
        Assert.That(model.Body, Is.EqualTo("Cached body"));
        await application.Received(1).ReadAsync(second[20], true, Arg.Any<CancellationToken>());
    }

    [AvaloniaTest]
    public async Task OpenMessageAsync_WhenHtmlReaderIsConfigured_RendersAutomaticallyWithoutLoadingImagesOrReloadingOnFlagChanges()
    {
        // Arrange
        IMailApplication application = Application();
        application.ReadAsync(Arg.Any<MessageSnapshot>(), true, Arg.Any<CancellationToken>())
            .Returns(Outcome<MimeSnapshot>.Success(new MimeSnapshot("Synthetic", "sender", null, "Text", "<p>HTML</p>", [])));
        IHtmlReadingService reader = Substitute.For<IHtmlReadingService>();
        reader.IsQualified.Returns(true);
        reader.ReadAsync(Arg.Any<MessageSnapshot>(), false, Arg.Any<CancellationToken>()).Returns(Failure.None);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow window = new MainWindow
        {
            DataContext = model
        };
        window.ConfigureHtml(reader);
        MessageSnapshot message = Message() with { Read = true };

        // Act
        await model.OpenMessageAsync(message);
        await window.WaitForActionsAsync();
        model.Selected = model.Selected! with { Flagged = true };
        await window.WaitForActionsAsync();

        // Assert
        await reader.Received(1).ReadAsync(message, false, Arg.Any<CancellationToken>());
        await reader.DidNotReceiveWithAnyArgs().LoadImagesAsync(default!, default);
        await application.Received(1).ReadAsync(message, true, Arg.Any<CancellationToken>());
        Assert.That(window.FindControl<MenuItem>("HtmlButton"), Is.Null);
        Assert.That(window.FindControl<MenuItem>("PlainButton"), Is.Null);
        Assert.That(window.FindControl<SelectableTextBlock>("PlainPreview")!.IsVisible, Is.False);
        await window.FlushComposeAsync();
    }

    [AvaloniaTest]
    public async Task OpenMessageAsync_WhenTheMailboxGenerationChanges_DoesNotOverwriteTheNewRow()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MessageSnapshot message = Message();
        TaskCompletionSource<Failure> mutation = new TaskCompletionSource<Failure>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.MutateAsync(message, MutationKind.SetRead, true, null, Arg.Any<CancellationToken>()).Returns(mutation.Task);
        model.Messages.Add(message);
        Task opening = model.OpenMessageAsync(message);
        MessageSnapshot replacement = message with
        {
            Provider = new ProviderIdentity(message.Account, "INBOX:8:1", new ImapIdentity(message.Account, "INBOX", 8, 1)),
            Read = true,
            Flagged = true
        };
        model.Messages[0] = replacement;
        model.Selected = replacement;

        // Act
        mutation.SetResult(Failure.None);
        await opening;

        // Assert
        Assert.That(model.Messages[0], Is.EqualTo(replacement));
        Assert.That(model.Selected, Is.EqualTo(replacement));
    }

    [AvaloniaTest]
    [TestCase(false, true, Failure.None, true)]
    [TestCase(false, true, Failure.Unavailable, false)]
    [TestCase(false, false, Failure.None, false)]
    [TestCase(true, true, Failure.None, true)]
    public async Task OpenMessageAsync_WhenReadStateVaries_QueuesOnlyUnreadWritableMail(
        bool read,
        bool writable,
        Failure failure,
        bool expectedRead
    )
    {
        // Arrange
        IMailApplication application = Application();
        MailboxSnapshot source = Source() with
        {
            Writable = writable
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(source));
        MessageSnapshot message = Message() with
        {
            Read = read
        };
        application.MutateAsync(message, MutationKind.SetRead, true, null, Arg.Any<CancellationToken>()).Returns(failure);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Messages.Add(message);
        model.RegroupMessages();

        // Act
        await model.OpenMessageAsync(message);
        await model.OpenMessageAsync(message);

        // Assert
        Assert.That(model.Selected!.Read, Is.EqualTo(expectedRead));
        Assert.That(model.Rows.Single().Unread, Is.EqualTo(!expectedRead));
        Assert.That(model.ReadAction, Is.EqualTo(Postfold.UI.Resources.Strings.Get(expectedRead ? "MarkUnread" : "MarkRead")));
        await application.Received(read || !writable ? 0 : failure == Failure.None ? 1 : 2).MutateAsync(
            message,
            MutationKind.SetRead,
            true,
            null,
            Arg.Any<CancellationToken>()
        );
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task FolderDrop_WhenTheTargetAccountVaries_AcceptsOnlyTheSelectedMessagesAccount(
        bool foreign
    )
    {
        // Arrange
        IMailApplication application = Application();
        MailboxSnapshot destination = Source() with
        {
            Account = new AccountId(foreign ? 2 : 1),
            Id = new FolderId(2),
            RemoteKey = "Archive",
            Name = "Archive",
            Role = MailboxRole.Archive
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(Source(), destination));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MessageSnapshot message = Message() with
        {
            Read = true
        };
        model.Messages.Add(message);
        model.RegroupMessages();
        model.Selected = message;
        MainWindow window = new MainWindow
        {
            DataContext = model
        };
        window.Configure(application, null);
        window.Show();
        try
        {
            NavigationItemViewModel folder = model.Navigation.SelectMany(item => item.Children)
                .Single(item => item.Scope.Account == destination.Account && item.Scope.Folder == destination.Id);
            Grid header = window.GetVisualDescendants().OfType<Grid>().Single(control =>
                control.Name == "PART_Header" && control.DataContext == folder);
            using DataTransfer data = new DataTransfer();
            data.Add(DataTransferItem.Create(DataFormat.CreateInProcessFormat<MessageSnapshot>("Postfold.Message"), message));
            DragEventArgs over = new DragEventArgs(DragDrop.DragOverEvent, data, header, default, KeyModifiers.None);
            DragEventArgs drop = new DragEventArgs(DragDrop.DropEvent, data, header, default, KeyModifiers.None);

            // Act
            header.RaiseEvent(over);
            header.RaiseEvent(drop);
            await window.FlushComposeAsync();

            // Assert
            Assert.That(over.DragEffects, Is.EqualTo(foreign ? DragDropEffects.None : DragDropEffects.Move));
            Assert.That(drop.DragEffects, Is.EqualTo(over.DragEffects));
            await application.Received(foreign ? 0 : 1).MutateAsync(
                message,
                MutationKind.Move,
                false,
                "Archive",
                Arg.Any<CancellationToken>()
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("ReadOnly")]
    [TestCase("Unselectable")]
    [TestCase("SameFolder")]
    [TestCase("ChangedSelection")]
    public async Task DropMessageAsync_WhenTheTargetIsInvalid_DoesNotRecordAMove(
        string reason
    )
    {
        // Arrange
        IMailApplication application = Application();
        MailboxSnapshot destination = Source() with
        {
            Id = new FolderId(2),
            RemoteKey = "Archive"
        };
        destination = reason switch
        {
            "ReadOnly" => destination with { Writable = false },
            "Unselectable" => destination with { Selectable = false },
            "SameFolder" => Source(),
            _ => destination
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(Source(), destination));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MessageSnapshot message = Message() with
        {
            Read = true
        };
        model.Selected = reason == "ChangedSelection" ? message with
        {
            Id = new MessageId(99)
        } : message;

        // Act
        bool allowed = model.CanDropMessage(message, destination);
        await model.DropMessageAsync(message, destination);

        // Assert
        Assert.That(allowed, Is.False);
        await application.DidNotReceive().MutateAsync(
            Arg.Any<MessageSnapshot>(),
            MutationKind.Move,
            false,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>()
        );
    }

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "owner@synthetic.invalid", "fixture");
        Capabilities capabilities = new Capabilities(false, false, false, true, false, false);
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(new AccountId(1), "One", endpoint, endpoint, capabilities),
            new AccountConfiguration(new AccountId(2), "Two", endpoint, endpoint, capabilities)
        ));
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(Source()));
        application.ReadAsync(Arg.Any<MessageSnapshot>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<MimeSnapshot>.Fail(Failure.NotFound));
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<SearchPage>.Success(new SearchPage([], null)));
        return application;
    }

    private static MailboxSnapshot Source()
    {
        return new MailboxSnapshot(new AccountId(1), new FolderId(1), "INBOX", "Inbox", MailboxRole.Inbox, true, true, 7);
    }

    private static MessageSnapshot Message()
    {
        AccountId account = new AccountId(1);
        return new MessageSnapshot(
            account,
            new MessageId(1),
            new ProviderIdentity(account, "INBOX:7:1", new ImapIdentity(account, "INBOX", 7, 1)),
            "Synthetic",
            "sender@synthetic.invalid",
            null,
            System.DateTimeOffset.UnixEpoch,
            100,
            false,
            false,
            ContentAvailability.Missing,
            PendingState.None
        );
    }
}

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
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Views;

public sealed class DurableWorkViewTests
{
    [AvaloniaTest]
    public async Task PendingActions_WhenRefreshingRows_HandlesClearedTemplateDataAndShowsCurrentEvidence()
    {
        // Arrange
        AccountId account = new AccountId(1);
        IMailApplication application = Application(account);
        IClientCatalog catalog = application.Catalog;
        MutationIntent intent = new MutationIntent(
            new OperationId(Guid.NewGuid()),
            account,
            new MessageId(1),
            new ProviderIdentity(account, "synthetic", null),
            MutationKind.SetRead,
            true,
            null
        );
        CommandSnapshot original = new CommandSnapshot(
            new OperationEntry(intent, PendingState.Queued, 1, 0, DateTimeOffset.UnixEpoch, Failure.None),
            "Original action"
        );
        CommandSnapshot updated = new CommandSnapshot(original.Entry with
        {
            State = PendingState.NeedsReview,
            Revision = 2,
            Evidence = Failure.NeedsReview
        }, "Updated action");
        catalog.CommandsAsync(account, Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(original));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        try
        {
            await main.CommandsAsync(account);
            Window pending = main.OwnedWindows.Single();
            ListBox list = pending.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "PendingCommands");
            Button refresh = pending.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Refresh");
            catalog.CommandsAsync(account, Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(updated));

            // Act
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            pending.UpdateLayout();
            Control? cleared = list.ItemTemplate!.Build(null);
            TextBlock row = (TextBlock)list.ItemTemplate.Build(list.Items.Single())!;

            // Assert
            Assert.That(list.Items.Single(), Is.EqualTo(updated));
            Assert.That(cleared, Is.Null);
            Assert.That(row.Text, Does.Contain("Updated action"));
            Assert.That(row.Text, Does.Contain(Strings.Describe(Failure.NeedsReview)));
            await application.DidNotReceiveWithAnyArgs().ResolveCommandAsync(default!, default, default);

            catalog.CommandsAsync(account, Arg.Any<CancellationToken>()).Returns(ImmutableArray<CommandSnapshot>.Empty);
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            pending.UpdateLayout();
            Assert.That(list.Items, Is.Empty);
            pending.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task WorkLists_WhenTemplateDataIsCleared_KeepTheEmptyListUsable(bool outbox)
    {
        // Arrange
        AccountId account = new AccountId(1);
        IMailApplication application = Application(account);
        application.Catalog.DraftsAsync(account, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<DraftSnapshot>.Empty);
        application.Catalog.OutboxAsync(account, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<OutboxEntry>.Empty);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        try
        {
            if (outbox)
            {
                await main.CommandsAsync(account);
            }
            else
            {
                await main.DraftsAsync(account);
            }
            Window window = main.OwnedWindows.Single();
            if (outbox)
            {
                window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 0;
                window.UpdateLayout();
            }
            ListBox list = window.GetVisualDescendants().OfType<ListBox>().Single(control => !outbox || control.Name == "SendIssues");

            // Act
            Control? cleared = list.ItemTemplate!.Build(null);
            window.UpdateLayout();

            // Assert
            Assert.That(list.Items, Is.Empty);
            Assert.That(cleared, Is.Null);
            Assert.That(window.IsVisible, Is.True);
            window.Close();
        }
        finally
        {
            main.Close();
        }
    }

    private static IMailApplication Application(AccountId account)
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "sender@synthetic.invalid", "synthetic");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new AccountConfiguration(account, "Synthetic", endpoint, endpoint)));
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxSnapshot>.Empty);
        catalog.CommandsAsync(account, Arg.Any<CancellationToken>()).Returns(ImmutableArray<CommandSnapshot>.Empty);
        catalog.MailboxUnreadCountsAsync(account, Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxUnreadCount>.Empty);
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([], null)));
        return application;
    }
}

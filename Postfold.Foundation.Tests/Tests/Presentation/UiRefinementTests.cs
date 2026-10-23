using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
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
using Postfold.Core.Models.Search;
using Postfold.UI.Models;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class UiRefinementTests
{
    [Test]
    [TestCase("Morgan Lee <morgan@synthetic.invalid>", "Morgan Lee", "morgan@synthetic.invalid")]
    [TestCase("\"River, Alex\" <alex@synthetic.invalid>", "River, Alex", "alex@synthetic.invalid")]
    [TestCase("bare@synthetic.invalid", "bare@synthetic.invalid", "bare@synthetic.invalid")]
    [TestCase("Malformed <sender", "Malformed <sender", "Malformed <sender")]
    [TestCase("One <one@synthetic.invalid>, Two <two@synthetic.invalid>",
        "One <one@synthetic.invalid>, Two <two@synthetic.invalid>", "One <one@synthetic.invalid>, Two <two@synthetic.invalid>")]
    public void Create_WhenFormattingSender_PreservesAddressesAndDoesNotInventAnIdentity(
        string input,
        string name,
        string address
    )
    {
        // Arrange
        string participants = input;

        // Act
        SenderPresentation sender = SenderPresentation.Create(participants);

        // Assert
        Assert.That(sender.Name, Is.EqualTo(name));
        Assert.That(sender.Address, Is.EqualTo(address));
    }

    [Test]
    public void Preview_WhenCachedTextHasParagraphs_NormalisesOnlyPresentationAndKeepsAttachmentAndPendingState()
    {
        // Arrange
        AccountId account = new AccountId(1);
        MessageSnapshot message = new MessageSnapshot(account, new MessageId(1), new ProviderIdentity(account, "one", null),
            "Subject", "Morgan Lee <morgan@synthetic.invalid>", null, DateTimeOffset.UnixEpoch, 10, false, false,
            ContentAvailability.Available, PendingState.NeedsReview, CachedPreview: " Hello,\r\n\r\n  synthetic\ttext ",
            HasKnownAttachments: true);

        // Act
        MessageSummary row = new MessageSummary(message, "", "Personal", "Yesterday");

        // Assert
        Assert.That(row.Sender, Is.EqualTo("Morgan Lee"));
        Assert.That(row.Preview, Is.EqualTo("Hello, synthetic text"));
        Assert.That(message.CachedPreview, Does.Contain("\r\n"));
        Assert.That(row.HasAttachments, Is.True);
        Assert.That(row.State, Does.Not.Contain("Personal"));
        Assert.That(row.State, Does.Not.Contain("Unread"));
        Assert.That(row.State, Is.Not.Empty);
        Assert.That(row.AccessibleName, Does.Contain("morgan@synthetic.invalid").And.Contain("Personal").And.Contain("Unread"));
    }

    [AvaloniaTest]
    public async Task RefreshAsync_WhenAnOldCountFinishesLate_KeepsTheCurrentFolderCount()
    {
        // Arrange
        IMailApplication application = Application();
        TaskCompletionSource<ImmutableArray<MailboxUnreadCount>> late = new TaskCompletionSource<ImmutableArray<MailboxUnreadCount>>();
        application.Catalog.MailboxUnreadCountsAsync(new AccountId(1), Arg.Any<CancellationToken>()).Returns(late.Task);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        Task first = model.InitialiseAsync();
        application.Catalog.MailboxUnreadCountsAsync(new AccountId(1), Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new MailboxUnreadCount(new AccountId(1), new FolderId(1), 4)));

        // Act
        await model.RefreshAsync(false);
        late.SetResult(ImmutableArray.Create(new MailboxUnreadCount(new AccountId(1), new FolderId(1), 99)));
        await first;

        // Assert
        NavigationItemViewModel folder = model.Navigation.Single(item => item.IsAccount).Children.Single();
        Assert.That(folder.UnreadCount, Is.EqualTo(4));
        Assert.That(folder.AccessibleName, Does.Contain("unread messages"));
    }

    [AvaloniaTest]
    public async Task OpenSettings_WhenContextIsUnified_ShowsAccountRowsAndReusesTheWindow()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Scope = model.Scopes.Single(scope => scope.Account is null && scope.InboxOnly);
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        try
        {
            // Act
            main.OpenSettings("Accounts");
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            main.OpenSettings("Accounts", new AccountId(1));

            // Assert
            Assert.That(settings.GetVisualDescendants().OfType<ComboBox>()
                .Any(control => control.Name == "SettingsAccountSelection"), Is.False);
            Assert.That(main.OwnedWindows.OfType<SettingsWindow>().Single(), Is.SameAs(settings));
            Assert.That(settings.GetVisualDescendants().OfType<TextBlock>()
                .Any(control => control.Text == "sender@synthetic.invalid"), Is.True);
            await model.InitialiseAsync();
            Assert.That(settings.GetVisualDescendants().OfType<Button>()
                .Count(control => control.Name == "RemoveAccount"), Is.EqualTo(1));
            settings.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task ApplyAppearance_WhenOnlyThemeChanges_PreservesSplitsAndKeepsWideReaderCommandsVisible()
    {
        // Arrange
        await using MainWindowViewModel model = new MainWindowViewModel();
        MainWindow main = new MainWindow { DataContext = model };
        main.Show();
        try
        {
            main.ApplyAppearance(AppearanceSettings.Default with
            {
                Theme = "Light"
            });
            Grid panes = main.FindControl<Grid>("Panes")!;
            panes.ColumnDefinitions[2].Width = new GridLength(500);

            // Act
            main.ApplyAppearance(AppearanceSettings.Default with
            {
                Theme = "Dark"
            });
            main.UpdateLayout();

            // Assert
            Assert.That(panes.ColumnDefinitions[2].Width, Is.EqualTo(new GridLength(500)));
            Assert.That(main.FindControl<Button>("ReplyButton")!.IsEffectivelyVisible, Is.True);
            Assert.That(main.FindControl<Button>("ReplyAllButton")!.IsEffectivelyVisible, Is.True);
            Assert.That(main.FindControl<Button>("ForwardButton")!.IsEffectivelyVisible, Is.True);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task Settings_WhenTheCatalogRefreshes_PreservesUnsavedIdentityEdits()
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.ConfigureIdentity(Substitute.For<IClientIdentity>());
        main.Show();
        try
        {
            main.OpenSettings("SenderIdentity", new AccountId(1));
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            TextBox sender = settings.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "Sender");
            sender.Text = "Edited <sender@synthetic.invalid>";

            // Act
            await model.InitialiseAsync();
            main.OpenSettings();

            // Assert
            Assert.That(sender.Text, Is.EqualTo("Edited <sender@synthetic.invalid>"));
            Assert.That(main.OwnedWindows.OfType<SettingsWindow>().Single(), Is.SameAs(settings));
            settings.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task Work_WhenChoosingAnAccountSubmenu_RoutesDraftsWithoutChangingUnifiedScope()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId chosen = new AccountId(2);
        AccountConfiguration first = (await application.Catalog.AccountsAsync(CancellationToken.None)).Single();
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(first, first with
        {
            Id = chosen,
            Name = "Work account"
        }));
        application.Catalog.DraftsAsync(chosen, Arg.Any<CancellationToken>()).Returns(ImmutableArray<DraftSnapshot>.Empty);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        model.Scope = model.Scopes.Single(scope => scope.Account is null && scope.InboxOnly);
        MailScope scope = model.Scope;
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        try
        {
            // Act
            Button work = main.FindControl<Button>("WorkButton")!;
            work.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MenuItem account = work.ContextMenu!.Items.OfType<MenuItem>().Single(item => (string)item.Header! == "Work account");
            account.Items.OfType<MenuItem>().Single(item => (string)item.Header! == Strings.Drafts)
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(model.Scope, Is.SameAs(scope));
            Assert.That(main.OwnedWindows.Single().Title, Is.EqualTo(Strings.Drafts));
            await application.Catalog.Received(1).DraftsAsync(chosen, Arg.Any<CancellationToken>());
            await application.Catalog.DidNotReceive().DraftsAsync(first.Id, Arg.Any<CancellationToken>());
            main.OwnedWindows.Single().Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(24)]
    [TestCase(120)]
    [TestCase(-8)]
    public async Task AccountHeader_WhenClickingAcrossTheRow_TogglesFoldersWithoutChangingMailScope(double horizontalPosition)
    {
        // Arrange
        IMailApplication application = Application();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.Show();
        try
        {
            main.UpdateLayout();
            TreeView navigation = main.FindControl<TreeView>("ScopeList")!;
            TreeViewItem account = (TreeViewItem)navigation.ContainerFromIndex(0)!;
            Grid header = account.GetVisualDescendants().OfType<Grid>().Single(control =>
                control.Name == "PART_Header" && control.TemplatedParent == account);
            Border selection = account.GetVisualDescendants().OfType<Border>().Single(control =>
                control.Name == "PART_LayoutRoot" && control.TemplatedParent == account);
            ToggleButton chevron = account.GetVisualDescendants().OfType<ToggleButton>().Single(control =>
                control.Name == "PART_ExpandCollapseChevron" && control.TemplatedParent == account);
            Point row = header.TranslatePoint(new Point(0, 0), main)!.Value;
            Point selected = selection.TranslatePoint(new Point(0, 0), main)!.Value;
            Point arrow = chevron.TranslatePoint(new Point(0, chevron.Bounds.Height / 2), main)!.Value;
            double x = horizontalPosition >= 0 ? horizontalPosition : header.Bounds.Width + horizontalPosition;
            Point click = row + new Vector(x, header.Bounds.Height / 2);
            MailScope scope = model.Scope!;

            // Act
            main.MouseDown(click, MouseButton.Left);
            main.MouseUp(click, MouseButton.Left);

            // Assert
            Assert.That(account.IsExpanded, Is.False);
            Assert.That(model.Navigation.First().IsExpanded, Is.False);
            Assert.That(model.Scope, Is.SameAs(scope));
            Assert.That(selected.Y, Is.EqualTo(row.Y).Within(1));
            Assert.That(arrow.Y, Is.EqualTo(row.Y + header.Bounds.Height / 2).Within(1));

            main.MouseDown(click, MouseButton.Left);
            main.MouseUp(click, MouseButton.Left);
            Assert.That(account.IsExpanded, Is.True);
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task Accounts_WhenActingOnASecondRow_RoutesEachActionToThatAccount()
    {
        // Arrange
        IMailApplication application = Application();
        AccountConfiguration first = (await application.Catalog.AccountsAsync(CancellationToken.None)).Single();
        AccountId second = new AccountId(2);
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(first, first with
        {
            Id = second,
            Incoming = first.Incoming! with { Username = "second@synthetic.invalid" }
        }));
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        AccountId? restored = null;
        AccountId? renamed = null;
        AccountId? removed = null;
        SettingsWindow settings = new SettingsWindow(
            model,
            AppearanceSettings.Default,
            null,
            null,
            null,
            _ => { },
            id =>
            {
                restored = id;
                return Task.CompletedTask;
            },
            id => renamed = id,
            id => removed = id,
            true,
            true
        );
        settings.SelectSection("Accounts");
        settings.Show();
        try
        {
            // Act
            Button[] actions = settings.GetVisualDescendants().OfType<Button>()
                .Where(button => Avalonia.Automation.AutomationProperties.GetName(button)?.EndsWith("second@synthetic.invalid", StringComparison.Ordinal) == true)
                .ToArray();
            foreach (Button action in actions)
            {
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            // Assert
            Assert.That(actions, Has.Length.EqualTo(3));
            Assert.That(restored, Is.EqualTo(second));
            Assert.That(renamed, Is.EqualTo(second));
            Assert.That(removed, Is.EqualTo(second));
            Assert.That(settings.GetVisualDescendants().OfType<ComboBox>(), Is.Empty);
        }
        finally
        {
            settings.Close();
        }
    }

    [AvaloniaTest]
    public async Task SenderIdentity_WhenSavingSecondAccount_PreservesOtherEditsAndUsesItsAccountId()
    {
        // Arrange
        IMailApplication application = Application();
        AccountConfiguration first = (await application.Catalog.AccountsAsync(CancellationToken.None)).Single();
        AccountId second = new AccountId(2);
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(first, first with
        {
            Id = second,
            Incoming = first.Incoming! with { Username = "second@synthetic.invalid" }
        }));
        IClientIdentity identity = Substitute.For<IClientIdentity>();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, null);
        main.ConfigureIdentity(identity);
        main.Show();
        try
        {
            main.OpenSettings("SenderIdentity");
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            TextBox[] senders = settings.GetVisualDescendants().OfType<TextBox>().Where(field => field.Name == "Sender").ToArray();
            senders[0].Text = "unsaved@synthetic.invalid";
            senders[1].Text = "second-edited@synthetic.invalid";

            // Act
            await model.InitialiseAsync();
            StackPanel fields = (StackPanel)senders[1].Parent!.Parent!;
            fields.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(senders, Has.Length.EqualTo(2));
            Assert.That(senders[0].Text, Is.EqualTo("unsaved@synthetic.invalid"));
            await identity.Received(1).SaveAsync(second, new ComposeIdentity("second-edited@synthetic.invalid"), Arg.Any<CancellationToken>());
            await identity.DidNotReceive().SaveAsync(first.Id, Arg.Any<ComposeIdentity>(), Arg.Any<CancellationToken>());
            Assert.That(settings.GetVisualDescendants().OfType<ComboBox>(), Is.Empty);
            settings.Close();
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(300, 1)]
    [TestCase(600, 0)]
    public async Task ListFilters_WhenPaneWidthChanges_StayReachableWithoutDuplicateMenuItems(double width, int row)
    {
        // Arrange
        await using MainWindowViewModel model = new MainWindowViewModel();
        MainWindow main = new MainWindow { DataContext = model };
        main.Show();
        try
        {
            // Act
            main.FindControl<Grid>("Panes")!.ColumnDefinitions[2].Width = new GridLength(width);
            main.UpdateLayout();

            // Assert
            StackPanel filters = main.FindControl<StackPanel>("ListFilters")!;
            Assert.That(filters.IsEffectivelyVisible, Is.True);
            Assert.That(Grid.GetRow(filters), Is.EqualTo(row));
            ContextMenu menu = main.FindControl<Button>("ListMoreButton")!.ContextMenu!;
            Assert.That(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, Strings.AllMail) || Equals(item.Header, Strings.Unread)), Is.False);
        }
        finally
        {
            main.Close();
        }
    }

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        AccountId account = new AccountId(1);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "sender@synthetic.invalid", "synthetic");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new AccountConfiguration(account, "Personal", endpoint, endpoint)));
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new MailboxSnapshot(account, new FolderId(1), "INBOX", "Inbox", MailboxRole.Inbox, true, true, 7)));
        catalog.MailboxUnreadCountsAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxUnreadCount>.Empty);
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Outcome<SearchPage>.Success(new SearchPage([], null)));
        return application;
    }
}

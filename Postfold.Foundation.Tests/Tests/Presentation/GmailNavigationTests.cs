using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Search;
using Postfold.UI.ViewModels;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class GmailNavigationTests
{
    [Test]
    public async Task InitialiseAsync_WhenGmailLabelsHaveExistingParents_PreservesHierarchySelectionCountsAndExpansion()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        ImmutableArray<MailboxSnapshot> labels =
        [
            Label(account, 1, "Banking"),
            Label(account, 2, "Banking/Switching/Welcome"),
            Label(account, 3, "Driving/Cars"),
            Label(account, 4, "Driving/Cars/MOT/Servicing"),
            Label(account, 5, "Housing"),
            Label(account, 6, "Housing/2024"),
            Label(account, 7, "Housing/2024/Receipts"),
            Label(new AccountId(2), 1, "Banking"),
            Label(new AccountId(2), 2, "Banking/Switching/Welcome")
        ];
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(labels);
        application.Catalog.MailboxUnreadCountsAsync(account, Arg.Any<CancellationToken>()).Returns(
            ImmutableArray.Create(new MailboxUnreadCount(account, new FolderId(7), 3)));
        await using MainWindowViewModel model = new MainWindowViewModel(application);

        // Act
        await model.InitialiseAsync();
        NavigationItemViewModel root = model.Navigation.Single(item => item.IsAccount && item.Scope.Account == account);
        NavigationItemViewModel banking = root.Children.Single(item => item.Name == "Banking");
        NavigationItemViewModel driving = root.Children.Single(item => item.Name == "Driving/Cars");
        NavigationItemViewModel year = root.Children.Single(item => item.Name == "Housing").Children.Single();
        NavigationItemViewModel receipts = year.Children.Single();
        year.IsExpanded = false;
        model.NavigationSelection = receipts;
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(labels.Add(Label(account, 8, "Other")));
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(account, "Renamed Google", null, null, ProviderKind: "Google"),
            new AccountConfiguration(new AccountId(2), "Other", null, null)));
        await model.InitialiseAsync();

        // Assert
        Assert.That(banking.Children.Single().Name, Is.EqualTo("Switching/Welcome"));
        Assert.That(driving.Children.Single().Name, Is.EqualTo("MOT/Servicing"));
        Assert.That(receipts.Name, Is.EqualTo("Receipts"));
        Assert.That(model.NavigationSelection!.Scope.Folder, Is.EqualTo(new FolderId(7)));
        Assert.That(model.NavigationSelection.CountText, Is.EqualTo("3"));
        Assert.That(model.Navigation.SelectMany(item => item.DescendantsAndSelf()).Single(item =>
            item.Scope.Account == year.Scope.Account && item.Scope.Folder == year.Scope.Folder).IsExpanded, Is.False);
        Assert.That(model.Navigation.Single(item => item.IsAccount && item.Scope.Account == new AccountId(2)).Children, Has.Length.EqualTo(2));
        Assert.That(model.Mailboxes.Single(item => item.Account == account && item.Id == new FolderId(7)).Name, Is.EqualTo("Housing/2024/Receipts"));
    }

    [TestCase("CATEGORY_PERSONAL", "Primary")]
    [TestCase("CATEGORY_PROMOTIONS", "Promotions")]
    [TestCase("CATEGORY_SOCIAL", "Social")]
    [TestCase("CATEGORY_UPDATES", "Updates")]
    [TestCase("CATEGORY_FORUMS", "Forums")]
    [TestCase("CHAT", "Chats")]
    [TestCase("TRASH", "Trash")]
    [TestCase("YELLOW_STAR", "Starred")]
    public async Task InitialiseAsync_WhenSystemLabelsAreCached_UsesFriendlyNamesWithoutChangingIds(string id, string expected)
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        MailboxSnapshot label = Label(account, 1, id) with
        {
            RemoteKey = id
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(label));
        await using MainWindowViewModel model = new MainWindowViewModel(application);

        // Act
        await model.InitialiseAsync();

        // Assert
        Assert.That(model.Navigation.Single(item => item.IsAccount && item.Scope.Account == account).Children.Single().Name, Is.EqualTo(expected));
        Assert.That(model.Mailboxes.Single().RemoteKey, Is.EqualTo(id));
        Assert.That(model.Mailboxes.Single().Name, Is.EqualTo(id));
    }

    [Test]
    public async Task InitialiseAsync_WhenBothStarLabelsExist_ShowsOneStarredFolderAndPreservesProviderMetadata()
    {
        // Arrange
        IMailApplication application = Application();
        AccountId account = new AccountId(1);
        MailboxSnapshot starred = Label(account, 1, "STARRED") with
        {
            RemoteKey = "STARRED"
        };
        MailboxSnapshot yellow = Label(account, 2, "YELLOW_STAR") with
        {
            RemoteKey = "YELLOW_STAR"
        };
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(starred, yellow));
        await using MainWindowViewModel model = new MainWindowViewModel(application);

        // Act
        await model.InitialiseAsync();

        // Assert
        NavigationItemViewModel[] folders = model.Navigation.Single(item => item.IsAccount && item.Scope.Account == account).Children.ToArray();
        Assert.That(folders, Has.Length.EqualTo(1));
        Assert.That(folders.Single().Name, Is.EqualTo("Starred"));
        Assert.That(folders.Single().Scope.Folder, Is.EqualTo(starred.Id));
        Assert.That(model.Mailboxes, Is.EquivalentTo(new[] { starred, yellow }));
    }

    private static MailboxSnapshot Label(AccountId account, long id, string name)
    {
        return new MailboxSnapshot(account, new FolderId(id), "Label_" + id, name, MailboxRole.Unresolved, true, true, 0);
    }

    private static IMailApplication Application()
    {
        IMailApplication application = Substitute.For<IMailApplication>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        application.Catalog.Returns(catalog);
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(new AccountId(1), "Google", null, null, ProviderKind: "Google"),
            new AccountConfiguration(new AccountId(2), "Other", null, null)));
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(Outcome<SearchPage>.Success(new SearchPage([], null)));
        return application;
    }
}

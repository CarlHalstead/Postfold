using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Desktop;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class AccountRemovalTests
{
    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfirmRemoveAccount_WhenOwnerConfirms_RefreshesNavigationOrExplainsProtectedDraft(bool draftExists)
    {
        // Arrange
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(), new EnginePolicy(false, false, false), TimeProvider.System,
            new EngineLimits(2, 8, 1, 100, 1048576, 100, 0, 0, 10485760), CancellationToken.None
        )).Value!;
        await host.SeedSyntheticAsync(30, CancellationToken.None);
        await host.RestoreAccountsAsync(new InMemoryCredentialStore(), CancellationToken.None);
        if (draftExists)
        {
            await host.Application.SaveDraftAsync(EngineFixture.Account, Guid.NewGuid(),
                new ComposeContent("sender@synthetic.invalid", [], "Preserve", "Draft text", DateTimeOffset.UnixEpoch,
                    "draft@synthetic.invalid"), CancellationToken.None);
        }
        await using MainWindowViewModel model = new MainWindowViewModel(host.Application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(host.Application, null, management: host);
        main.Show();
        await model.InitialiseAsync();
        try
        {
            // Act
            main.OpenSettings("Accounts", EngineFixture.Account);
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RemoveAccount")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            Window dialog = main.OwnedWindows.Single(window => window is not SettingsWindow);
            Assert.That(model.Accounts, Has.Count.EqualTo(3));
            dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRemoveAccount")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            settings.Close();

            // Assert
            Assert.That(model.Accounts.Count, Is.EqualTo(draftExists ? 3 : 2));
            if (draftExists)
            {
                Assert.That(dialog.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "RemoveAccountStatus").Text,
                    Does.Contain("drafts or unresolved"));
            }
            else
            {
                Assert.That(model.Scopes.Any(scope => scope.Account == EngineFixture.Account), Is.False);
                Assert.That(main.OwnedWindows, Is.Empty);
            }
        }
        finally
        {
            foreach (Window child in main.OwnedWindows.ToArray())
            {
                child.Close();
            }
            main.Close();
        }
    }

    [Test]
    public async Task RemoveAsync_WhenAccountHasOnlyCache_DisconnectsWithoutDeletingAndStaysRemovedAfterRestart()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        EnginePolicy policy = new EnginePolicy(false, false, false);
        AccountId account = new AccountId(3);
        await using (DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            directory, policy, TimeProvider.System, new Postfold.Core.Models.Synchronisation.EngineLimits(2, 8, 1, 100, 1048576, 100, 0, 0, 10485760), CancellationToken.None
        )).Value!)
        {
            await host.SeedSyntheticAsync(30, CancellationToken.None);
            await host.RestoreAccountsAsync(new InMemoryCredentialStore(), CancellationToken.None);

            // Act
            Failure result = await host.RemoveAsync(account, CancellationToken.None);

            // Assert
            Assert.That(result, Is.EqualTo(Failure.None));
            Assert.That(host.Find(account), Is.Null);
            Assert.That(await host.Catalog.AccountsAsync(CancellationToken.None), Has.Length.EqualTo(2));
            Assert.That(await host.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(20));
            Assert.That((await host.Query.SearchAsync(new SearchRequest(null, true, "", null, false, 100),
                CancellationToken.None)).Value!.Messages, Has.Length.EqualTo(20));
            Assert.That(await host.NextAccountIdAsync(CancellationToken.None), Is.EqualTo(new AccountId(4)));
            Assert.That(File.Exists(Path.Combine(directory, "profile.db")), Is.True);
        }
        await using DesktopMailRuntime reopened = (await DesktopMailRuntime.OpenAsync(
            directory, policy, TimeProvider.System, new Postfold.Core.Models.Synchronisation.EngineLimits(2, 8, 1, 100, 1048576, 100, 0, 0, 10485760), CancellationToken.None
        )).Value!;
        await reopened.RestoreAccountsAsync(new InMemoryCredentialStore(), CancellationToken.None);
        Assert.That(reopened.Find(account), Is.Null);
        Assert.That(await reopened.Catalog.AccountsAsync(CancellationToken.None), Has.Length.EqualTo(2));
        Assert.That(await reopened.NextAccountIdAsync(CancellationToken.None), Is.EqualTo(new AccountId(4)));
    }

    [Test]
    public async Task RemoveAsync_WhenDraftExists_RefusesAndRestoresUsableEngineWithoutLosingDraft()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            directory, new EnginePolicy(false, false, false), TimeProvider.System,
            new Postfold.Core.Models.Synchronisation.EngineLimits(2, 8, 1, 100, 1048576, 100, 0, 0, 10485760), CancellationToken.None
        )).Value!;
        await host.SeedSyntheticAsync(30, CancellationToken.None);
        await host.RestoreAccountsAsync(new InMemoryCredentialStore(), CancellationToken.None);
        ComposeContent content = new ComposeContent("sender@synthetic.invalid", [], "Preserve", "Draft text",
            DateTimeOffset.UnixEpoch, "draft@synthetic.invalid");
        Guid id = Guid.NewGuid();
        await host.Application.SaveDraftAsync(EngineFixture.Account, id, content, CancellationToken.None);

        // Act
        Failure result = await host.RemoveAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(host.Find(EngineFixture.Account), Is.Not.Null);
        Assert.That(await host.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None), Has.Length.EqualTo(1));
        Assert.That((await host.Application.SaveDraftAsync(EngineFixture.Account, id, content, CancellationToken.None)).IsSuccess, Is.True);
    }
}

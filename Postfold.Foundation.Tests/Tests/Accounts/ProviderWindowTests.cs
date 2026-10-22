using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Models.Authentication;
using Postfold.Desktop.Services;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.UI.Controls;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class ProviderWindowTests
{
    [AvaloniaTest]
    [TestCase("Google", Failure.None)]
    [TestCase("Microsoft", Failure.Transient)]
    public async Task SignIn_WhenAccountIsRetained_UsesBrowserIdentityAndClosesWithoutWaitingForSync(string provider, Failure synchronisation)
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup, IProviderAccounts>();
        IProviderAccounts providers = (IProviderAccounts)setup;
        providers.Providers.Returns([new ProviderAvailability(provider, Failure.None)]);
        providers.ConnectProviderAsync(Arg.Any<ProviderSetupRequest>(), Arg.Any<CancellationToken>()).Returns(
            new ProviderConnectionResult(Failure.None, new AccountId(1), synchronisation));
        AccountSetupWindow window = new AccountSetupWindow(setup);
        AccountId? added = null;
        window.AccountAdded += id => added = id;
        window.Show();
        try
        {
            // Act
            Click(window, "SignIn" + provider);
            await FormControls.DrainAsync(window);

            // Assert
            Assert.That(added, Is.EqualTo(new AccountId(1)));
            Assert.That(window.IsVisible, Is.False);
            await providers.Received(1).ConnectProviderAsync(Arg.Is<ProviderSetupRequest>(request =>
                request.ProviderKind == provider && request.Address == "" && request.Incoming == null && request.Outgoing == null && !request.Synchronise), Arg.Any<CancellationToken>());
        }
        finally
        {
            window.Close();
            await FormControls.DrainAsync(window);
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task OpenMessageAsync_WhenBodyIsMissing_DownloadsAutomaticallyAndThenUsesCache(bool htmlOnly)
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        if (htmlOnly)
        {
            provider.Raw = GmailFixture.Encode("From: sender@example.test\r\nTo: synthetic@example.test\r\nSubject: HTML\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Readable HTML body</p><img src=\"https://remote.invalid/tracker\">");
        }
        await using DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(), new EnginePolicy(true, false, false), TimeProvider.System,
            new EngineLimits(2, 32, 1, 10, 1048576, 50, 0, 0, 1048576), CancellationToken.None)).Value!;
        runtime.ConfigureProviderClients(new ProviderClientConfiguration(GmailFixture.Binding.ClientId, GooglePolicyDecisionReference: "synthetic decision"));
        runtime.GoogleHttpFactory = provider.Http;
        await runtime.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), provider.Authentication, provider.Authentication, CancellationToken.None);
        await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google"), CancellationToken.None);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MessageSnapshot message = model.Messages.Single(item => item.Provider.Key == "one");

        // Act
        await model.OpenMessageAsync(message);
        string body = model.Body;
        int requests = provider.Requests.Count;
        provider.Intercept = (_, _) => Task.FromResult<System.Net.Http.HttpResponseMessage?>(GmailFixture.Reply("{}", System.Net.HttpStatusCode.ServiceUnavailable));
        await model.ReadSelectedAsync(true);

        // Assert
        Assert.That(body, Does.Contain(htmlOnly ? "Readable HTML body" : "frozen synthetic body"));
        Assert.That(body, Does.Not.Contain("<p>"));
        Assert.That(model.Body, Is.EqualTo(body));
        Assert.That(provider.Requests.Count, Is.EqualTo(requests));
        Assert.That(provider.Requests.Count(item => item.Path == "messages/one?format=raw"), Is.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase("Google", false, 620, 16)]
    [TestCase("Microsoft", true, 360, 24)]
    public async Task ProviderChoice_WhenUnconfigured_ClearsAndHidesPasswordAndExplainsUnavailableConnection(
        string kind,
        bool dark,
        double width,
        double fontSize
    )
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        window.Width = width;
        window.FontSize = fontSize;
        window.Show();
        try
        {
            Control<TextBox>(window, "IncomingPassword").Text = "synthetic unused password";

            // Act
            Click(window, "SignIn" + kind);
            await FormControls.DrainAsync(window);

            // Assert
            Assert.That(Control<TextBox>(window, "IncomingPassword").Text, Is.Empty);
            Assert.That(Control<TextBox>(window, "IncomingPassword").IsEffectivelyVisible, Is.False);
            Assert.That(Control<TextBlock>(window, "SetupStatus").Text, Is.EqualTo(Strings.Get("ProviderConfigurationMissing")));
            Assert.That(Control<TextBox>(window, "Email").IsEffectivelyVisible, Is.False);
            Assert.That(AutomationProperties.GetName(Control<Button>(window, "SignIn" + kind)), Is.EqualTo(Strings.Get("SignIn" + kind)));
            window.UpdateLayout();
            Assert.That(Control<Button>(window, "SignIn" + kind).Bounds.Width, Is.LessThanOrEqualTo(window.ClientSize.Width));
            await setup.DidNotReceive().DiscoverAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await setup.DidNotReceive().AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task ConnectProvider_WhenActualFormCompletes_UsesSharedEngineForArchiveTrashReadAndStarWithoutFabricatedArchive()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(),
            new EnginePolicy(true, false, false),
            TimeProvider.System,
            new EngineLimits(2, 32, 1, 10, 1048576, 50, 0, 0, 1048576),
            CancellationToken.None
        )).Value!;
        runtime.ConfigureProviderClients(new ProviderClientConfiguration(
            GmailFixture.Binding.ClientId,
            GooglePolicyDecisionReference: "synthetic-only policy decision"
        ));
        runtime.GoogleHttpFactory = provider.Http;
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        await runtime.RestoreAccountsAsync(passwords, provider.Authentication, provider.Authentication, CancellationToken.None);
        AccountSetupWindow window = new AccountSetupWindow(new AccountSetup(runtime, passwords, runtime.Identity));
        TaskCompletionSource<AccountId> added = new TaskCompletionSource<AccountId>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.AccountAdded += id => added.TrySetResult(id);
        window.Show();
        try
        {
            Assert.That(provider.Authentication.Authorization, Is.Null);

            // Act
            Click(window, "SignInGoogle");
            AccountId account = await added.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await FormControls.DrainAsync(window);
            Assert.That(window.IsVisible, Is.False);
            await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
            await model.InitialiseAsync();
            await model.SynchroniseAccountAsync(account);
            await model.RefreshAsync(false);
            MessageSnapshot message = model.Messages.Single(item => item.Provider.Key == "one");
            await model.OpenMessageAsync(message);
            int priorMutations = provider.Requests.Count(item => item.Method == "POST" && item.Path == "messages/one/modify");
            Assert.That(model.ArchiveCommand.CanExecute(null), Is.True);
            await model.ArchiveCommand.ExecuteAsync(null);
            Assert.That(model.Messages.Any(item => item.Provider.Key == "one"), Is.False);
            MailboxSnapshot label = model.Mailboxes.Single(item => item.Account == account && item.RemoteKey == "Label_1");
            model.Scope = model.Scopes.Single(item => item.Account == account && item.Folder == label.Id);
            await model.RefreshAsync(false);
            model.Selected = model.Messages.Single(item => item.Provider.Key == "one");
            await model.ReadCommand.ExecuteAsync(null);
            await model.FlagCommand.ExecuteAsync(null);
            await model.TrashCommand.ExecuteAsync(null);

            // Assert
            Assert.That(provider.Messages["one"], Is.EquivalentTo(new[] { "Label_1", "UNREAD", "STARRED", "TRASH" }));
            Assert.That(model.Mailboxes.Any(item => item.Role == MailboxRole.Archive), Is.False);
            Assert.That(provider.Requests.Count(item => item.Method == "POST" && item.Path == "messages/one/modify") - priorMutations, Is.EqualTo(4));
            Assert.That(provider.Sends, Is.Zero);
            Assert.That(provider.DraftWrites, Is.Zero);
            Assert.That(passwords.ReceivedCalls(), Is.Empty);
            Assert.That((await runtime.Catalog.AccountsAsync(CancellationToken.None)).Single().Id, Is.EqualTo(account));
            AccountConfiguration configured = model.Accounts[0];
            model.Accounts[0] = configured with { Capabilities = null };
            Assert.That(model.ArchiveCommand.CanExecute(null), Is.False);
            Assert.That(model.ReadCommand.CanExecute(null), Is.False);
            model.Accounts[0] = configured;
            model.Selected = message with { Id = new MessageId(long.MaxValue) };
            Assert.That(model.ArchiveCommand.CanExecute(null), Is.False);
            Assert.That(model.TrashCommand.CanExecute(null), Is.False);
        }
        finally
        {
            window.Close();
            await FormControls.DrainAsync(window);
        }
    }

    [AvaloniaTest]
    public async Task ProviderAccountWindow_WhenExplicitActionsRun_UsesDistinctPortsAndReportsLocalDeletionHonestly()
    {
        // Arrange
        IProviderAccounts providers = Substitute.For<IProviderAccounts>();
        AccountConfiguration account = new AccountConfiguration(
            new AccountId(1),
            "synthetic",
            null,
            null,
            ProviderKind: "Google",
            GoogleBinding: GmailFixture.Binding
        );
        providers.ReconnectProviderAsync(account.Id, Arg.Any<CancellationToken>()).Returns(new ProviderConnectionResult(Failure.NeedsAuthentication, account.Id));
        ProviderAccountWindow window = new ProviderAccountWindow(providers, account, () => Task.CompletedTask);
        window.Show();
        try
        {
            Assert.That(Control<TextBlock>(window, "ProviderAccountStatus").Text, Is.EqualTo(Strings.Get("ProviderNotChecked")));

            // Act
            Click(window, "ReconnectProvider");
            await FormControls.DrainAsync(window);
            Assert.That(Control<TextBlock>(window, "ProviderAccountStatus").Text, Does.Contain(Strings.Describe(Failure.NeedsAuthentication)));
            Click(window, "DeleteProviderTokens");
            await FormControls.DrainAsync(window);

            // Assert
            Assert.That(Control<TextBlock>(window, "ProviderAccountStatus").Text, Is.EqualTo(Strings.Get("ProviderTokensDeleted")));
            await providers.Received(1).ReconnectProviderAsync(account.Id, Arg.Any<CancellationToken>());
            await providers.Received(1).DeleteProviderTokensAsync(account.Id, Arg.Any<CancellationToken>());
            await providers.DidNotReceive().RevokeProviderAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            window.Close();
            await FormControls.DrainAsync(window);
        }
    }

    [AvaloniaTest]
    public void ComposeWindow_WhenDraftHeldWithoutConflict_ExplainsReviewHoldAndDisablesSend()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        ComposeContent content = new ComposeContent(
            GmailFixture.Binding.Address,
            ["other@example.test"],
            "synthetic",
            "retained",
            DateTimeOffset.UtcNow,
            "synthetic@invalid"
        );
        ComposeWindow window = new ComposeWindow(application, new AccountId(1), content, held: true);
        window.Show();
        try
        {
            // Act / Assert
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text == Strings.Get("DraftHeld")), Is.True);
            Assert.That(Control<Button>(window, "Send").IsEnabled, Is.False);
            Assert.That(window.GetVisualDescendants().OfType<TextBlock>().Any(item => item.Text == Strings.Conflict), Is.False);
        }
        finally
        {
            window.ConfirmShutdown();
            window.Close();
        }
    }

    private static T Control<T>(Window window, string name) where T : Control
    {
        return window.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static void Click(
        Window window,
        string name
    )
    {
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}

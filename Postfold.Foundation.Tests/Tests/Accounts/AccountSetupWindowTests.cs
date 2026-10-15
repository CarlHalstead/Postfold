using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.UI.Resources;
using Postfold.UI.Views;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class AccountSetupWindowTests
{
    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public void ContinueServers_WhenLookupCompletes_PopulatesOnlyDiscoveredSettingsAndPreservesEdits(bool found)
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.DiscoverAsync("owner@synthetic.invalid", Arg.Any<CancellationToken>()).Returns(
            found ? new AccountServerSettings(
                new MailEndpoint("imap.synthetic.invalid", 143, TlsMode.RequiredStartTls, "owner@synthetic.invalid", ""),
                new MailEndpoint("smtp.synthetic.invalid", 587, TlsMode.RequiredStartTls, "owner@synthetic.invalid", "")
            ) : new AccountServerSettings(null, null)
        );
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        try
        {
            Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";

            // Act
            Click(window, "ContinueServers");

            // Assert
            Assert.That(Control<TextBox>(window, "IncomingHost").Text, Is.EqualTo(found ? "imap.synthetic.invalid" : ""));
            Assert.That(Control<TextBox>(window, "OutgoingHost").Text, Is.EqualTo(found ? "smtp.synthetic.invalid" : ""));
            Assert.That(Control<TextBox>(window, "IncomingPort").Text, Is.EqualTo(found ? "143" : "993"));
            Assert.That(Control<TextBox>(window, "OutgoingPort").Text, Is.EqualTo(found ? "587" : "465"));
            Assert.That(Control<ComboBox>(window, "IncomingTls").SelectedItem, Is.EqualTo(found ? TlsMode.RequiredStartTls : TlsMode.Implicit));
            Assert.That(Control<TextBlock>(window, "SetupStatus").Text, Is.Empty);
            Control<TextBox>(window, "IncomingHost").Text = "manual.synthetic.invalid";
            Click(window, "BackLogin");
            Click(window, "ContinueServers");
            Assert.That(Control<TextBox>(window, "IncomingHost").Text, Is.EqualTo("manual.synthetic.invalid"));
            setup.Received(1).DiscoverAsync("owner@synthetic.invalid", Arg.Any<CancellationToken>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TestEndpoints_WhenSharedCredentialsAreSubmitted_UsesEmailNameAndSecureStorage()
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AccountSetupResult(Failure.None, Failure.NeedsAuthentication, Failure.None, [])
        );
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        try
        {
            Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
            Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
            Click(window, "ContinueServers");
            Control<TextBox>(window, "IncomingHost").Text = "mail.synthetic.invalid";
            Control<TextBox>(window, "OutgoingHost").Text = "mail.synthetic.invalid";

            // Act
            Click(window, "TestEndpoints");

            // Assert
            setup.Received(1).AddAsync(Arg.Is<AccountSetupRequest>(request =>
                request.Name == "owner@synthetic.invalid" && request.Incoming.Username == "owner@synthetic.invalid" &&
                request.Outgoing.Username == "owner@synthetic.invalid" && request.Incoming.Port == 993 &&
                request.Outgoing.Port == 465 && request.Incoming.Tls == TlsMode.Implicit &&
                request.Outgoing.Tls == TlsMode.Implicit), Arg.Any<CancellationToken>());
            Assert.That(
                Control<TextBlock>(window, "SetupStatus").Text,
                Does.Contain("IMAP: " + Strings.Describe(Failure.NeedsAuthentication))
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TestEndpoints_WhenEmailAndPasswordAreEditedBeforeSubmission_UsesCurrentCredentials()
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AccountSetupResult(Failure.None, Failure.NeedsAuthentication, Failure.None, [])
        );
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        try
        {
            Control<TextBox>(window, "Email").Text = "original@synthetic.invalid";
            Control<TextBox>(window, "IncomingPassword").Text = "original-secret";
            Control<TextBox>(window, "Email").Text = "latest@synthetic.invalid";
            Control<TextBox>(window, "IncomingPassword").Text = "latest-secret";

            // Act
            Click(window, "ContinueServers");
            Click(window, "TestEndpoints");

            // Assert
            setup.Received(1).AddAsync(Arg.Is<AccountSetupRequest>(request =>
                request.Sender == "latest@synthetic.invalid" && request.Incoming.Username == "latest@synthetic.invalid" &&
                request.Outgoing.Username == "latest@synthetic.invalid" && request.IncomingPassword == "latest-secret" &&
                request.OutgoingPassword == "latest-secret"), Arg.Any<CancellationToken>());
            Assert.That(Control<TextBox>(window, "IncomingPassword").Text, Is.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void Setup_WhenOpened_OffersOnlySharedCredentialsAndNoAccountNameOrStorageChoice()
    {
        // Arrange
        AccountSetupWindow window = new AccountSetupWindow(Substitute.For<IAccountSetup>());
        window.Show();
        try
        {
            // Act
            window.UpdateLayout();
            string?[] names = window.GetVisualDescendants().OfType<Control>().Select(control => control.Name).ToArray();

            // Assert
            Assert.That(names, Does.Not.Contain("Name"));
            Assert.That(names, Does.Not.Contain("SessionOnly"));
            Assert.That(names, Does.Not.Contain("SharedCredentials"));
            Assert.That(names, Does.Not.Contain("OutgoingPassword"));
            Assert.That(window.GetVisualDescendants().OfType<Expander>(), Is.Empty);
            Assert.That(window.GetVisualDescendants().OfType<TextBox>().Count(field => field.IsEffectivelyVisible), Is.EqualTo(2));
            Assert.That(names, Does.Contain("IncomingPassword"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(Failure.Validation)]
    [TestCase(Failure.TlsRejected)]
    [TestCase(Failure.NeedsAuthentication)]
    [TestCase(Failure.CredentialStorageUnavailable)]
    [TestCase(Failure.PolicyBlocked)]
    [TestCase(Failure.Unsupported)]
    public void TestEndpoints_WhenBackendReportsExpectedFailure_ShowsDistinctDiagnostic(Failure failure)
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AccountSetupResult(Failure.None, failure, Failure.None, [])
        );
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        try
        {
            // Act
            Click(window, "TestEndpoints");

            // Assert
            Assert.That(
                Control<TextBlock>(window, "SetupStatus").Text,
                Does.Contain("IMAP: " + Strings.Describe(failure))
            );
            Assert.That(window.GetVisualDescendants().OfType<Control>().Select(control => control.Name), Does.Not.Contain("SaveRoles"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TestEndpoints_WhenPortIsInvalid_ReportsValidationWithoutCallingSetup()
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        try
        {
            Control<TextBox>(window, "IncomingPort").Text = "invalid";

            // Act
            Click(window, "TestEndpoints");

            // Assert
            Assert.That(Control<TextBlock>(window, "SetupStatus").Text, Is.EqualTo(Strings.Describe(Failure.Validation)));
            setup.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
            Assert.That(
                AutomationProperties.GetLiveSetting(Control<TextBlock>(window, "SetupStatus")),
                Is.EqualTo(AutomationLiveSetting.Polite)
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void TestEndpoints_WhenIndependentSettingsAreSubmitted_PreservesChoicesAndClearsPasswords()
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(new AccountSetupResult(
            Failure.CredentialStorageUnavailable,
            Failure.TlsRejected,
            Failure.NeedsAuthentication,
            []
        ));
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        try
        {
            Control<TextBox>(window, "IncomingHost").Text = "incoming.synthetic.invalid";
            Control<TextBox>(window, "IncomingPassword").Text = "read-secret";
            Control<ComboBox>(window, "IncomingTls").SelectedItem = TlsMode.RequiredStartTls;
            Control<TextBox>(window, "OutgoingHost").Text = "outgoing.synthetic.invalid";
            Control<TextBox>(window, "OutgoingPort").Text = "587";

            // Act
            Click(window, "TestEndpoints");

            // Assert
            setup.Received(1).AddAsync(Arg.Is<AccountSetupRequest>(request =>
                request.Incoming.Host == "incoming.synthetic.invalid" && request.Incoming.Username == "owner@synthetic.invalid" &&
                request.Incoming.Tls == TlsMode.RequiredStartTls && request.Incoming.Port == 993 &&
                request.Outgoing.Host == "outgoing.synthetic.invalid" && request.Outgoing.Username == "owner@synthetic.invalid" &&
                request.Outgoing.Port == 587 && request.IncomingPassword == "read-secret" &&
                request.OutgoingPassword == "read-secret"), Arg.Any<CancellationToken>());
            Assert.That(Control<TextBox>(window, "IncomingPassword").Text, Is.Empty);
            string diagnostic = Control<TextBlock>(window, "SetupStatus").Text!;
            Assert.That(diagnostic, Does.Contain(Strings.Describe(Failure.CredentialStorageUnavailable)));
            Assert.That(diagnostic, Does.Contain("IMAP: " + Strings.Describe(Failure.TlsRejected)));
            Assert.That(diagnostic, Does.Contain("SMTP: " + Strings.Describe(Failure.NeedsAuthentication)));
            Assert.That(diagnostic, Does.Not.Contain("secret"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(Failure.None, Failure.None, Failure.None, true)]
    [TestCase(Failure.NeedsAuthentication, Failure.None, Failure.None, false)]
    [TestCase(Failure.None, Failure.Validation, Failure.None, false)]
    [TestCase(Failure.None, Failure.None, Failure.Transient, false)]
    public void TestEndpoints_WhenAccountIsAdded_ClosesOnlyAfterAllChecksSucceed(
        Failure outgoing,
        Failure identity,
        Failure synchronisation,
        bool closed
    )
    {
        // Arrange
        AccountId account = new AccountId(1);
        MailboxSnapshot inbox = new MailboxSnapshot(
            account,
            new FolderId(1),
            "INBOX",
            "Inbox",
            MailboxRole.Inbox,
            true,
            true,
            1
        );
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(
            new AccountSetupResult(Failure.None, Failure.None, outgoing, [inbox], identity, synchronisation)
        );
        AccountSetupWindow window = new AccountSetupWindow(setup);
        AccountId? added = null;
        window.AccountAdded += value => added = value;
        window.Show();
        Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        try
        {
            // Act
            Click(window, "TestEndpoints");

            // Assert
            Assert.That(window.IsVisible, Is.EqualTo(!closed));
            Assert.That(added, Is.EqualTo(account));
            setup.DidNotReceiveWithAnyArgs().SetRolesAsync(default, default!, default);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Close_WhenSetupIsPending_CancelsOwnedRequestAndClearsPasswords()
    {
        // Arrange
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        TaskCompletionSource<AccountSetupResult> pending = new TaskCompletionSource<AccountSetupResult>();
        CancellationToken requestToken = default;
        setup.AddAsync(Arg.Any<AccountSetupRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            requestToken = call.Arg<CancellationToken>();
            requestToken.Register(() => pending.TrySetCanceled(requestToken));
            return pending.Task;
        });
        AccountSetupWindow window = new AccountSetupWindow(setup);
        window.Show();
        Control<TextBox>(window, "Email").Text = "owner@synthetic.invalid";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        Control<TextBox>(window, "IncomingPassword").Text = "synthetic-secret";
        Click(window, "TestEndpoints");

        // Act
        window.Close();
        try
        {
            await pending.Task;
        }
        catch (OperationCanceledException)
        {
        }

        // Assert
        Assert.That(requestToken.IsCancellationRequested, Is.True);
        Assert.That(Control<TextBox>(window, "IncomingPassword").Text, Is.Empty);
    }

    private static TControl Control<TControl>(Window window, string name) where TControl : Avalonia.Controls.Control
    {
        window.UpdateLayout();
        return window.GetVisualDescendants().OfType<TControl>().Single(control => control.Name == name);
    }

    private static void Click(Window window, string name)
    {
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}

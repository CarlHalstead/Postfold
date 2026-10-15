using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class AccountCredentialsTests
{
    [AvaloniaTest]
    [TestCase(Failure.None)]
    [TestCase(Failure.CredentialStorageUnavailable)]
    public async Task SaveCredentials_WhenSubmitted_UsesSecureRestorationAndClearsPasswords(Failure result)
    {
        // Arrange
        AccountId account = new AccountId(1);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic", "synthetic");
        AccountConfiguration configuration = new AccountConfiguration(account, "Synthetic account", endpoint, endpoint);
        IMailApplication application = Substitute.For<IMailApplication>();
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(configuration));
        IAccountSetup setup = Substitute.For<IAccountSetup>();
        setup.RestoreCredentialsAsync(account, "synthetic-secret", "synthetic-secret", Arg.Any<CancellationToken>()).Returns(result);
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        model.Accounts.Add(configuration);
        MainWindow main = new MainWindow { DataContext = model };
        main.Configure(application, setup);
        main.Show();
        try
        {
            main.OpenSettings("Accounts", account);
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "Credentials")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            Window credentials = main.OwnedWindows.Single(window => window is not SettingsWindow);
            TextBox password = credentials.GetVisualDescendants().OfType<TextBox>().Single(field => field.Name == "IncomingPassword");
            password.Text = "synthetic-secret";

            // Act
            credentials.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveCredentials")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(password.Text, Is.Empty);
            Assert.That(credentials.GetVisualDescendants().OfType<CheckBox>().Select(control => control.Name),
                Is.EqualTo(new[] { "SharedCredentials" }));
            Assert.That(credentials.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                block.Text == (result == Failure.None ? Strings.Get("CredentialsSaved") : Strings.Describe(result))), Is.True);
            await setup.Received(1).RestoreCredentialsAsync(account, "synthetic-secret", "synthetic-secret", Arg.Any<CancellationToken>());
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
}

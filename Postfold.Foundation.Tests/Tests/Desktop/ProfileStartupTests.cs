using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Services;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class ProfileStartupTests
{
    [AvaloniaTest]
    public async Task StartFresh_WhenDesktopStartupWasBlocked_OpensSetupAndRemembersTheNewProfile()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        string previous = Path.Combine(profiles, "default");
        Directory.CreateDirectory(previous);
        string evidence = Path.Combine(previous, "draft.mime");
        await File.WriteAllTextAsync(evidence, "Synthetic preserved draft");
        IClassicDesktopStyleApplicationLifetime desktop = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        TaskCompletionSource shutdown = new TaskCompletionSource();
        desktop.When(lifetime => lifetime.Shutdown(0)).Do(_ => shutdown.SetResult());
        MainWindow main = new MainWindow { DataContext = new MainWindowViewModel() };
        DesktopSession session = new DesktopSession(desktop, main, profiles);
        main.Show();
        try
        {
            await session.Startup;
            Window recovery = main.OwnedWindows.Single();

            // Act
            recovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "StartFresh")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await session.Startup;
            main.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            string selected = new ProfileSelectionStore(profiles).ReadDirectory();
            Assert.That(selected, Is.Not.EqualTo(previous));
            Assert.That(File.Exists(Path.Combine(selected, "profile.db")), Is.True);
            Assert.That(await File.ReadAllTextAsync(evidence), Is.EqualTo("Synthetic preserved draft"));
            Assert.That(main.OwnedWindows.OfType<AccountSetupWindow>().Count(), Is.EqualTo(1));
            Assert.That(main.OwnedWindows.OfType<SettingsWindow>().Count(), Is.EqualTo(1));
            Assert.That(((MainWindowViewModel)main.DataContext!).Status, Is.Empty);
        }
        finally
        {
            foreach (Window child in main.OwnedWindows.ToArray())
            {
                child.Close();
            }
            main.Close();
            await shutdown.Task;
            main.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(Failure.NeedsReview)]
    [TestCase(Failure.Unavailable)]
    [TestCase(Failure.NewerSchema)]
    public async Task ReportStartupFailure_WhenProfileCannotOpen_ExplainsRecoveryAndKeepsSettingsUsable(Failure failure)
    {
        // Arrange
        await using MainWindowViewModel model = new MainWindowViewModel();
        MainWindow main = new MainWindow { DataContext = model };
        int freshRequests = 0;
        main.FreshProfileRequested += (_, _) => freshRequests++;
        main.Show();
        try
        {
            // Act
            main.ReportStartupFailure(failure);
            Window recovery = main.OwnedWindows.Single();
            recovery.Close();
            main.FindControl<Button>("SetupButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
            settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveAppearance")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();
            main.OwnedWindows.Single(window => window is not SettingsWindow).GetVisualDescendants()
                .OfType<Button>().Single(button => button.Name == "StartFresh")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Assert
            Assert.That(model.Status, Is.EqualTo(Strings.Get("ProfileStartupHint")));
            Assert.That(main.OwnedWindows.Single(window => window is not SettingsWindow).GetVisualDescendants()
                .OfType<TextBlock>().Any(block => block.Text == Strings.Describe(failure)), Is.False);
            Assert.That(model.HasStatus, Is.True);
            Assert.That(settings.GetVisualDescendants().OfType<TextBlock>().Any(block =>
                block.Text == Strings.Get("AppearanceSessionOnly")), Is.True);
            Assert.That(freshRequests, Is.EqualTo(1));
            Assert.That(main.OwnedWindows.OfType<AccountSetupWindow>(), Is.Empty);
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

    [AvaloniaTest]
    [TestCase(true)]
    [TestCase(false)]
    public async Task OpenProfilesFolder_WhenClicked_UsesTheNativeLauncherAndReportsFailure(bool succeeds)
    {
        // Arrange
        string directory = Path.Combine(EngineFixture.DirectoryPath(), "profiles with spaces");
        Directory.CreateDirectory(directory);
        ILauncher launcher = Substitute.For<ILauncher>();
        launcher.LaunchFileAsync(Arg.Any<IStorageItem>()).Returns(succeeds);
        await using MainWindowViewModel model = new MainWindowViewModel();
        MainWindow main = new MainWindow(launcher) { DataContext = model };
        main.Show();
        try
        {
            main.ReportStartupFailure(Failure.NeedsReview, profilesDirectory: directory);
            Window recovery = main.OwnedWindows.Single();

            // Act
            recovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenProfilesFolder")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            await launcher.Received(1).LaunchFileAsync(Arg.Is<IStorageItem>(item =>
                Path.TrimEndingDirectorySeparator(item.Path.LocalPath) == directory));
            Assert.That(recovery.GetVisualDescendants().OfType<TextBlock>()
                .Single(block => block.Name == "ProfileStartupStatus").Text,
                Is.EqualTo(succeeds ? "" : Strings.Get("OpenProfilesFolderFailed")));
        }
        finally
        {
            main.OwnedWindows.Single().Close();
            main.Close();
        }
    }

    [AvaloniaTest]
    public async Task Configure_WhenFreshProfileIsReady_WiresBothButtonsOnceAndOpensAccountSetup()
    {
        // Arrange
        await using MainWindowViewModel model = new MainWindowViewModel();
        MainWindow main = new MainWindow { DataContext = model };
        main.Show();
        try
        {
            main.ReportStartupFailure(Failure.NeedsReview);
            main.BeginProfileStartup();
            bool setupWhileStarting = main.FindControl<Button>("SetupButton")!.IsEnabled;
            bool settingsWhileStarting = main.FindControl<Button>("SettingsButton")!.IsEnabled;
            main.Configure(Substitute.For<IMailApplication>(), Substitute.For<IAccountSetup>());

            // Act
            main.FindControl<Button>("SetupButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.WaitForActionsAsync();

            // Assert
            Assert.That(setupWhileStarting, Is.False);
            Assert.That(settingsWhileStarting, Is.False);
            Assert.That(main.OwnedWindows.OfType<AccountSetupWindow>().Count(), Is.EqualTo(1));
            Assert.That(main.OwnedWindows.OfType<SettingsWindow>().Count(), Is.EqualTo(1));
        }
        finally
        {
            foreach (Window child in main.OwnedWindows.ToArray())
            {
                child.Close();
            }
            await main.WaitForActionsAsync();
            main.Close();
        }
    }

    [Test]
    public async Task SaveDirectory_WhenStartingFresh_ReopensTheNewProfileAndPreservesPreviousFiles()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string profiles = Path.Combine(directory, "profiles");
        string previous = Path.Combine(profiles, "default");
        Directory.CreateDirectory(previous);
        string database = Path.Combine(previous, "profile.db");
        string draft = Path.Combine(previous, "draft.mime");
        await File.WriteAllTextAsync(database, "Synthetic unsupported profile");
        await File.WriteAllTextAsync(draft, "Synthetic unsent draft");
        ProfileSelectionStore selection = new ProfileSelectionStore(profiles);
        Assert.That(selection.ReadDirectory(), Is.EqualTo(previous));
        string fresh = selection.NewDirectory();
        EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);

        // Act
        await using (DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            fresh,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!)
        {
            selection.SaveDirectory(fresh);
        }
        string selected = new ProfileSelectionStore(profiles).ReadDirectory();
        await using DesktopMailRuntime reopened = (await DesktopMailRuntime.OpenAsync(
            selected,
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;

        // Assert
        Assert.That(selected, Is.EqualTo(fresh));
        Assert.That(await reopened.Application.Catalog.AccountsAsync(CancellationToken.None), Is.Empty);
        Assert.That(await File.ReadAllTextAsync(database), Is.EqualTo("Synthetic unsupported profile"));
        Assert.That(await File.ReadAllTextAsync(draft), Is.EqualTo("Synthetic unsent draft"));
    }

    [Test]
    [TestCase("../outside")]
    [TestCase("fresh-../outside")]
    public void ReadDirectory_WhenSelectionIsNotALocalFreshProfile_RejectsIt(string name)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        File.WriteAllText(Path.Combine(directory, "active-profile.txt"), name);
        ProfileSelectionStore selection = new ProfileSelectionStore(directory);

        // Act
        Action read = () => selection.ReadDirectory();

        // Assert
        Assert.That(read, Throws.TypeOf<InvalidDataException>());
    }
}

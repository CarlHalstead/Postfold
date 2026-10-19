using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Services.Updates;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateSettingsTests
{
    [AvaloniaTest]
    public async Task Updates_WhenNativeHandoffIsUnqualified_ShowsUnavailableControlsAndRetainsUserChoices()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        IDesktopIntegration desktop = Substitute.For<IDesktopIntegration>();
        desktop.ReadPreferencesAsync(Arg.Any<CancellationToken>()).Returns(new DesktopPreferences(false, true)
        {
            Updates = new UpdatePreferences(false, false, true)
            {
                CheckIntervalHours = 168
            }
        });
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null, desktop);
        main.Show();
        main.OpenSettings("Updates");
        SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
        try
        {
            MethodInfo drain = typeof(SettingsWindow).Assembly.GetType("Postfold.UI.Controls.FormControls")!
                .GetMethod("DrainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task)drain.Invoke(null, new object[] { main })!;

            // Act
            bool? checks = settings.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "UpdateChecksEnabled").IsChecked;
            bool? downloads = settings.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "UpdateDownloadsEnabled").IsChecked;
            bool? unmetered = settings.GetVisualDescendants().OfType<CheckBox>().Single(control => control.Name == "UpdateUnmeteredOnly").IsChecked;
            Find<CheckBox>(settings, "UpdateChecksEnabled").Focus();
            settings.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            settings.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            IInputElement? beforeFirstChoice = settings.FocusManager?.GetFocusedElement();
            Find<CheckBox>(settings, "UpdateChecksEnabled").Focus();
            settings.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            settings.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            IInputElement? afterFirstTab = settings.FocusManager?.GetFocusedElement();
            settings.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            settings.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            IInputElement? afterSecondTab = settings.FocusManager?.GetFocusedElement();

            // Assert
            Assert.That(checks, Is.False);
            Assert.That(downloads, Is.False);
            Assert.That(unmetered, Is.True);
            Assert.That(beforeFirstChoice, Is.SameAs(settings.GetVisualDescendants().OfType<TabItem>().Single(tab => tab.IsSelected)));
            Assert.That(afterFirstTab, Is.SameAs(Find<CheckBox>(settings, "UpdateDownloadsEnabled")));
            Assert.That(afterSecondTab, Is.SameAs(Find<Button>(settings, "SaveUpdatePreferences")));
            foreach (string name in new[] { "UpdateChecksEnabled", "UpdateDownloadsEnabled", "UpdateUnmeteredOnly" })
            {
                CheckBox choice = Find<CheckBox>(settings, name);
                Assert.That(choice.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text), Does.Contain(Strings.Get(name)), name);
            }
            Assert.That(settings.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "UpdateSchedule").SelectedIndex, Is.EqualTo(1));
            Assert.That(settings.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "UpdatesAvailability").Text, Is.EqualTo(Strings.Get("UpdatesUnavailable")));
            TextBlock currentVersion = Find<TextBlock>(settings, "CurrentVersion");
            Assert.That(currentVersion.IsVisible, Is.True);
            Assert.That(currentVersion.Text, Is.EqualTo(Strings.Get("CurrentVersion") + ": " + FileVersionInfo.GetVersionInfo(typeof(SettingsWindow).Assembly.Location).ProductVersion!.Split('+')[0]));
            Assert.That(Find<TextBlock>(settings, "UpdateVersion").IsVisible, Is.False);
            foreach (string name in new[] { "CheckForUpdates", "DownloadUpdate", "RestartToUpdate" })
            {
                Assert.That(settings.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name).IsEnabled, Is.False);
            }
        }
        finally
        {
            settings.Close();
            main.Close();
            await model.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public async Task Updates_WhenDownloadIsCancelled_ReportsRealProgressAndWaitsForCancellationBeforeReadmittingActions()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        IDesktopIntegration desktop = Substitute.For<IDesktopIntegration>();
        desktop.ReadPreferencesAsync(Arg.Any<CancellationToken>()).Returns(DesktopPreferences.Default);
        IClientUpdates updates = Substitute.For<IClientUpdates>();
        updates.Current.Returns(new UpdateStatus(UpdateStage.Available, "0.2.0"));
        TaskCompletionSource<UpdateStatus> completion = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<UpdateStatus>? progress = null;
        CancellationToken operation = default;
        updates.DownloadAsync(Arg.Any<IProgress<UpdateStatus>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            progress = call.ArgAt<IProgress<UpdateStatus>>(0);
            operation = call.ArgAt<CancellationToken>(1);
            return completion.Task;
        });
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null, desktop, updates: updates);
        main.Show();
        main.OpenSettings("Updates");
        SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
        await DrainAsync(main);
        try
        {
            // Act
            Assert.That(Find<TextBlock>(settings, "CurrentVersion").Text, Is.EqualTo(Strings.Get("CurrentVersion") + ": " + FileVersionInfo.GetVersionInfo(typeof(SettingsWindow).Assembly.Location).ProductVersion!.Split('+')[0]));
            Assert.That(Find<TextBlock>(settings, "UpdateVersion").Text, Is.EqualTo(Strings.Get("UpdateVersion") + ": 0.2.0"));
            Find<Button>(settings, "DownloadUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            progress!.Report(new UpdateStatus(UpdateStage.Downloading, "0.2.0", 53));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(Find<ProgressBar>(settings, "UpdateProgress").Value, Is.EqualTo(53));
            Assert.That(Find<ProgressBar>(settings, "UpdateProgress").IsIndeterminate, Is.False);
            Find<Button>(settings, "CancelUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Assert: cancellation remains pending until the owned operation acknowledges it.
            Assert.That(operation.IsCancellationRequested, Is.True);
            Assert.That(Find<Button>(settings, "CheckForUpdates").IsEnabled, Is.False);
            Assert.That(Find<TextBlock>(settings, "UpdatesAvailability").Text, Is.EqualTo(Strings.Get("UpdateCancelling")));
            completion.SetCanceled(operation);
            await DrainAsync(main);
            Assert.That(Find<TextBlock>(settings, "UpdatesAvailability").Text, Is.EqualTo(Strings.Get("UpdateStateCancelled")));
            Assert.That(Find<Button>(settings, "RestartToUpdate").IsEnabled, Is.False);
            Assert.That(Find<Button>(settings, "CheckForUpdates").IsEnabled, Is.True);
            progress.Report(new UpdateStatus(UpdateStage.Ready, "0.2.0"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(Find<Button>(settings, "RestartToUpdate").IsEnabled, Is.False);
            await updates.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
            await updates.DidNotReceiveWithAnyArgs().RestartAsync(default);
        }
        finally
        {
            completion.TrySetCanceled();
            settings.Close();
            await DrainAsync(main);
            main.Close();
            await model.DisposeAsync();
        }
    }

    [Test]
    public async Task InactiveService_WhenAnyOperationIsRequested_NeverLeavesInactiveStateOrReportsSimulatedProgress()
    {
        // Arrange
        InactiveClientUpdates updates = new InactiveClientUpdates();
        IProgress<UpdateStatus> progress = Substitute.For<IProgress<UpdateStatus>>();

        // Act
        UpdateStatus check = await updates.CheckAsync(progress, CancellationToken.None);
        UpdateStatus download = await updates.DownloadAsync(progress, CancellationToken.None);
        UpdateStatus restart = await updates.RestartAsync(CancellationToken.None);

        // Assert
        Assert.That(check.Stage, Is.EqualTo(UpdateStage.Inactive));
        Assert.That(download, Is.EqualTo(check));
        Assert.That(restart, Is.EqualTo(check));
        progress.DidNotReceiveWithAnyArgs().Report(default!);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Updates_WhenRestartCannotFinish_ShowsTruthfulRecoveryGuidanceInItsLiveStatus(bool committed)
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        IDesktopIntegration desktop = Substitute.For<IDesktopIntegration>();
        desktop.ReadPreferencesAsync(Arg.Any<CancellationToken>()).Returns(DesktopPreferences.Default);
        IClientUpdates updates = Substitute.For<IClientUpdates>();
        updates.Current.Returns(new UpdateStatus(UpdateStage.Ready, "1.1.0")
        {
            CanRestart = true
        });
        updates.RestartAsync(Arg.Any<CancellationToken>()).Returns(new UpdateStatus(committed ? UpdateStage.RestartCommitted : UpdateStage.RestartBlocked, "1.1.0", Failure: committed ? Failure.NeedsReview : Failure.Unavailable)
        {
            CanRestart = !committed,
            Explanation = committed ? null : Strings.Get("UpdateShutdownDraftBlocked")
        });
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null, desktop, updates: updates);
        main.Show();
        main.OpenSettings("Updates");
        SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
        await DrainAsync(main);
        try
        {
            // Act
            Find<Button>(settings, "RestartToUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Assert
            TextBlock status = Find<TextBlock>(settings, "UpdatesAvailability");
            Assert.That(status.Text, Does.Contain(committed ? Strings.Get("UpdateStateRestartCommitted") : Strings.Get("UpdateShutdownDraftBlocked")));
            Assert.That(status.Text, Does.Not.Contain(Strings.Get("UpdateVerificationFailed")));
            Assert.That(AutomationProperties.GetLiveSetting(status), Is.EqualTo(AutomationLiveSetting.Polite));
            Assert.That(Find<Button>(settings, "RestartToUpdate").IsEnabled, Is.EqualTo(!committed));
            Assert.That(Find<Button>(settings, "CheckForUpdates").IsEnabled, Is.EqualTo(!committed));
            Assert.That(settings.IsEnabled, Is.True);
        }
        finally
        {
            settings.Close();
            main.Close();
            await model.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public async Task Updates_WhenPreferencesAreSaved_PassesWeeklyScheduleWithoutStartingDiscovery()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        IDesktopIntegration desktop = Substitute.For<IDesktopIntegration>();
        desktop.ReadPreferencesAsync(Arg.Any<CancellationToken>()).Returns(DesktopPreferences.Default);
        IClientUpdates updates = Substitute.For<IClientUpdates>();
        updates.Current.Returns(new UpdateStatus(UpdateStage.Inactive));
        MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow main = new MainWindow
        {
            DataContext = model
        };
        main.Configure(application, null, desktop, updates: updates);
        main.Show();
        main.OpenSettings("Updates");
        SettingsWindow settings = main.OwnedWindows.OfType<SettingsWindow>().Single();
        await DrainAsync(main);
        try
        {
            // Act
            Find<ComboBox>(settings, "UpdateSchedule").SelectedIndex = 1;
            Find<Button>(settings, "SaveUpdatePreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await DrainAsync(main);

            // Assert
            await desktop.Received(1).ConfigureUpdatesAsync(Arg.Is<UpdatePreferences>(preferences => preferences.CheckIntervalHours == 168), Arg.Any<CancellationToken>());
            await updates.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
            await updates.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default);
            await updates.DidNotReceiveWithAnyArgs().RestartAsync(default);
        }
        finally
        {
            settings.Close();
            main.Close();
            await model.DisposeAsync();
        }
    }

    private static T Find<T>(SettingsWindow settings, string name) where T : Control
    {
        return settings.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    }

    private static Task DrainAsync(MainWindow main)
    {
        MethodInfo drain = typeof(SettingsWindow).Assembly.GetType("Postfold.UI.Controls.FormControls")!
            .GetMethod("DrainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (Task)drain.Invoke(null, new object[] { main })!;
    }
}

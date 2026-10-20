using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services;
using Postfold.Desktop.Services.Updates;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.Storage.Stores;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateSchedulingTests
{
    [AvaloniaTest]
    public async Task DesktopSession_WhenPowerResumes_ReevaluatesElapsedPolicyWithoutPrematureOrDuplicateChecks()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        lifetime.When(value => value.Shutdown(Arg.Any<int>())).Do(_ => stopped.TrySetResult());
        DesktopSession session = fixture.CreateSession(lifetime, fixture.Directory + "-session-profiles", clock, new Connection(UpdateConnection.Unknown));
        session.Window.Show();
        try
        {
            await session.Startup;
            await session.ObservePowerMessage(0x0218, new IntPtr(0x12)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(fixture.Requests, Has.Count.EqualTo(3));

            // Act
            clock.Advance(TimeSpan.FromHours(23));
            await session.ObservePowerMessage(0x0218, new IntPtr(0x12));
            await session.ObservePowerMessage(0x0218, new IntPtr(0x12));

            // Assert
            Assert.That(fixture.Requests, Has.Count.EqualTo(3));
            clock.Advance(TimeSpan.FromHours(1));
            await session.ObservePowerMessage(0x0218, new IntPtr(0x12)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(fixture.Requests, Has.Count.EqualTo(6));
            Assert.That(fixture.Requests, Does.Not.Contain(ClientUpdateFixture.BundleName));
        }
        finally
        {
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            session.Window.Close();
        }
    }

    [AvaloniaTest]
    public async Task DesktopSession_WhenClosingDuringAutomaticDownload_ObservesCancellationBeforeShutdownAndProfileRelease()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (name, _) => Task.FromResult(name == ClientUpdateFixture.BundleName ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new LateStream(fixture.Signer.Package, reached, release))
        } : fixture.Respond(name));
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        lifetime.When(value => value.Shutdown(Arg.Any<int>())).Do(_ => stopped.TrySetResult());
        string profiles = fixture.Directory + "-session-profiles";
        DesktopSession session = fixture.CreateSession(lifetime, profiles, clock, new Connection(UpdateConnection.Unmetered));
        session.Window.Show();
        try
        {
            await session.Startup;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Window.OpenSettings("Updates");
            SettingsWindow settings = session.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Act
            session.Window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            // Assert
            Assert.That(stopped.Task.IsCompleted, Is.False);
            Assert.That(settings.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RestartToUpdate").IsEnabled, Is.False);
            release.TrySetResult();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string database = Path.Combine(profiles, "default", "profile.db");
            using FileStream exclusive = new FileStream(database + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.That(File.Exists(Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName)), Is.False);
            Assert.That(Directory.GetFiles(Path.Combine(fixture.Directory, "packages"), "*.partial"), Is.Empty);
        }
        finally
        {
            release.TrySetResult();
            session.Window.Close();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(24)]
    [TestCase(168)]
    public async Task Scheduler_WhenFirstStartedAndResumed_ChecksOnlyAtSavedElapsedInterval(int hours)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unmetered);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false)
        {
            CheckIntervalHours = hours
        });
        Assert.That(fixture.Requests, Is.Empty, "Loading Settings must not start discovery.");
        Assert.That(Directory.Exists(fixture.Directory), Is.False);

        // Act
        await ui.StartAsync();
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available), "The first authenticated check must succeed before testing its saved interval.");
        int first = fixture.Requests.Count;
        clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        await ui.SettleAsync();

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(first, Is.EqualTo(3));
        Assert.That(fixture.Requests.Count, Is.EqualTo(first));
        clock.Advance(TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        Assert.That(fixture.Requests.Count, Is.EqualTo(first * 2));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task Scheduler_WhenPreferencesAndLastCheckAreReopened_PreservesPolicyAndAvoidsPrematureCheck()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unmetered);
        await using (Harness first = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false)))
        {
            await first.StartAsync();
            await first.SaveAsync(new UpdatePreferences(false, false, true)
            {
                CheckIntervalHours = 168
            });
            Assert.That((await first.Profile.ReadDesktopPreferencesAsync(CancellationToken.None)).Updates.CheckIntervalHours, Is.EqualTo(168));
        }
        int requests = fixture.Requests.Count;
        clock.Advance(TimeSpan.FromDays(2));

        // Act
        await using Harness reopened = await Harness.OpenAsync(fixture, clock, connection);
        await reopened.StartAsync();
        Assert.That(reopened.Button("CheckForUpdates").IsEnabled, Is.False);
        await reopened.SaveAsync(new UpdatePreferences(DownloadsEnabled: false)
        {
            CheckIntervalHours = 168
        });
        await reopened.SettleAsync();

        // Assert
        Assert.That(fixture.Requests.Count, Is.EqualTo(requests));
        await reopened.SaveAsync(new UpdatePreferences(DownloadsEnabled: false));
        await reopened.SettleAsync();
        Assert.That(fixture.Requests.Count, Is.EqualTo(requests + 3), "Shortening the interval makes the elapsed check due.");
        Assert.That(reopened.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
    }

    [AvaloniaTest]
    public async Task Scheduler_WhenClockTimerExpires_ChecksWithoutAnActivationOrPollingLoop()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        await using Harness ui = await Harness.OpenAsync(fixture, clock, new Connection(UpdateConnection.Unmetered), new UpdatePreferences(DownloadsEnabled: false));
        await ui.StartAsync();
        TaskCompletionSource checkedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Service.Changed += (_, _) =>
        {
            if (fixture.Requests.Count == 6 && !ui.Service.IsBusy)
            {
                checkedAgain.TrySetResult();
            }
        };

        // Act
        clock.Advance(TimeSpan.FromDays(1));
        await checkedAgain.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(fixture.Requests, Has.Count.EqualTo(6));
    }

    [AvaloniaTest]
    public async Task Scheduler_WhenFailuresRepeatAndApplicationRestarts_PreservesBoundedRetryDeadline()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unmetered);
        fixture.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await using (Harness first = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false)))
        {
            await first.StartAsync();
            for (int attempt = 0; attempt < 10; attempt++)
            {
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, attempt)));
                Assert.That(first.Service.Current.RetryAt, Is.EqualTo(clock.GetUtcNow() + delay));
                clock.Advance(delay - TimeSpan.FromTicks(1));
                await first.SettleAsync();
                Assert.That(fixture.Requests, Has.Count.EqualTo(attempt + 1));
                clock.Advance(TimeSpan.FromTicks(1));
                await first.SettleAsync();
            }
        }
        int requests = fixture.Requests.Count;

        // Act
        await using Harness reopened = await Harness.OpenAsync(fixture, clock, connection);
        await reopened.StartAsync();
        await reopened.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(reopened.Service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(fixture.Requests, Has.Count.EqualTo(requests));
        Assert.That(reopened.Find<TextBlock>("UpdatesAvailability").Text, Does.Contain("Retry available after"));
        fixture.Response = null;
        clock.Advance(TimeSpan.FromHours(1));
        await reopened.SettleAsync();
        Assert.That(reopened.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(reopened.Service.Current.RetryAt, Is.Null);
        Assert.That(fixture.Requests, Has.Count.EqualTo(requests + 3));
    }

    [AvaloniaTest]
    public async Task Settings_WhenChecksAreDisabledDuringLateDiscovery_CancelsAutomaticWorkWithoutRevivingAvailable()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Offline);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false));
        await ui.StartAsync();
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<HttpResponseMessage> release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool networkOnDispatcher = true;
        fixture.Response = (_, _) =>
        {
            networkOnDispatcher = Dispatcher.UIThread.CheckAccess();
            reached.TrySetResult();
            return release.Task;
        };
        connection.Set(UpdateConnection.Unmetered);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await ui.SaveAsync(new UpdatePreferences(false, false, true));
            Task cancelled = ui.Service.CancelAsync();

            // Assert
            Assert.That(ui.Service.IsCancelling, Is.True);
            Assert.That(cancelled.IsCompleted, Is.False);
            Assert.That(networkOnDispatcher, Is.False);
            release.TrySetResult(fixture.Respond("Postfold.win-x64-stable.manifest.json"));
            await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.SettleAsync();
            Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Cancelled));
            Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
            Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
            fixture.Response = null;
            await ui.SaveAsync(new UpdatePreferences(DownloadsEnabled: false));
            await ui.ClickAsync("CheckForUpdates");
            Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        }
        finally
        {
            release.TrySetResult(fixture.Respond("Postfold.win-x64-stable.manifest.json"));
            await ui.Service.CancelAsync();
        }
    }

    [AvaloniaTest]
    [TestCase("Offline", true, false)]
    [TestCase("Unknown", true, false)]
    [TestCase("Metered", true, false)]
    [TestCase("Unmetered", true, true)]
    [TestCase("Unknown", false, true)]
    [TestCase("Metered", false, true)]
    public async Task Operations_WhenConnectionCostChanges_UseSameManualAndAutomaticDownloadPolicy(
        string connectionCost,
        bool unmeteredOnly,
        bool permitted
    )
    {
        // Arrange
        UpdateConnection cost = Enum.Parse<UpdateConnection>(connectionCost);
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(cost);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(UnmeteredOnly: unmeteredOnly));

        // Act
        await ui.StartAsync();

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(cost == UpdateConnection.Offline ? UpdateStage.Idle : permitted ? UpdateStage.Ready : UpdateStage.Available));
        Assert.That(fixture.Requests.Contains(ClientUpdateFixture.BundleName), Is.EqualTo(permitted));
        if (cost == UpdateConnection.Offline)
        {
            Assert.That((await ui.Service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.Unavailable));
            Assert.That(fixture.Requests, Is.Empty);
        }
        else
        {
            await ui.ClickAsync("CheckForUpdates");
            await ui.ClickAsync("DownloadUpdate");
            Assert.That(ui.Service.Current.Stage, Is.EqualTo(permitted ? UpdateStage.Ready : UpdateStage.Failed));
            Assert.That(ui.Service.Current.Failure, Is.EqualTo(permitted ? Failure.None : Failure.PolicyBlocked));
        }
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        connection.Set(UpdateConnection.Unmetered);
        await ui.SettleAsync();
        await ui.ClickAsync("CheckForUpdates");
        await ui.ClickAsync("DownloadUpdate");
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Ready));
    }

    [AvaloniaTest]
    public async Task Scheduler_WhenDisabledOrOffline_DoesNotPollAndObservesPersistedReenablement()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Offline);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(false, false, true));
        await ui.StartAsync();

        // Act
        clock.Advance(TimeSpan.FromDays(30));
        connection.Set(UpdateConnection.Unmetered);
        await ui.SettleAsync();

        // Assert
        Assert.That(fixture.Requests, Is.Empty);
        Assert.That((await ui.Service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.PolicyBlocked));
        await ui.SaveAsync(new UpdatePreferences(DownloadsEnabled: false));
        await ui.SettleAsync();
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(fixture.Requests, Does.Not.Contain(ClientUpdateFixture.BundleName));
        Assert.That((await ui.Service.DownloadAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(fixture.Requests, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Scheduler_WhenTransportFails_UsesBoundedBackoffAndRetriesSuccessfully(bool rateLimited)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unmetered);
        fixture.Response = (_, _) =>
        {
            HttpResponseMessage response = new HttpResponseMessage(rateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.InternalServerError);
            if (rateLimited)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(10));
            }
            return Task.FromResult(response);
        };
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false));

        // Act
        await ui.StartAsync();
        TimeSpan backoff = TimeSpan.FromSeconds(rateLimited ? 3600 : 30);
        clock.Advance(backoff - TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(fixture.Requests, Has.Count.EqualTo(1));
        fixture.Response = null;
        clock.Advance(TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(fixture.Requests, Has.Count.EqualTo(4));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That((await ui.Service.DownloadAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.PolicyBlocked), "Download permission is not bypassed by a successful retry.");
    }

    [AvaloniaTest]
    public async Task Check_WhenCachedMetadataExpires_ReauthenticatesBytesAndHonoursDurableTrust()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        await using Harness ui = await Harness.OpenAsync(fixture, clock, new Connection(UpdateConnection.Unmetered), new UpdatePreferences(DownloadsEnabled: false));
        await ui.StartAsync();

        // Act
        await ui.ClickAsync("CheckForUpdates");
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(fixture.Requests, Has.Count.EqualTo(3));
        fixture.Documents["Postfold.win-x64-stable.manifest.json"] = [1];
        clock.Advance(TimeSpan.FromTicks(1));
        await ui.ClickAsync("CheckForUpdates");
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That(fixture.Requests.Count, Is.GreaterThan(3));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task Download_WhenTransportFails_RetriesAfterCooldownAndReverifiesCachedPackageBeforeReuse()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unknown);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, UpdatePreferences.Default);
        await ui.StartAsync();
        connection.Set(UpdateConnection.Unmetered);
        await ui.SettleAsync();
        fixture.Response = (name, _) =>
        {
            HttpResponseMessage response = name == ClientUpdateFixture.BundleName ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : fixture.Respond(name);
            if (name == ClientUpdateFixture.BundleName)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(15));
            }
            return Task.FromResult(response);
        };

        // Act
        await ui.ClickAsync("DownloadUpdate");

        // Assert
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Service.Current.Failure, Is.EqualTo(Failure.Transient));
        Assert.That(ui.Service.Current.RetryAt, Is.EqualTo(clock.GetUtcNow() + TimeSpan.FromMinutes(15)));
        int failedRequests = fixture.Requests.Count;
        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        await ui.ClickAsync("CheckForUpdates");
        Assert.That(fixture.Requests.Count, Is.EqualTo(failedRequests));
        fixture.Response = null;
        clock.Advance(TimeSpan.FromTicks(1));
        await ui.SettleAsync();
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Ready));
        string package = Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName);
        Assert.That(File.ReadAllBytes(package), Is.EqualTo(fixture.Signer.Package));
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(package)!, "*.partial"), Is.Empty);
        File.WriteAllBytes(package, [1, 2, 3]);
        await ui.ClickAsync("CheckForUpdates");
        int cachedRequests = fixture.Requests.Count;
        await ui.ClickAsync("DownloadUpdate");
        Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Service.Current.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(File.ReadAllBytes(package), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(fixture.Requests.Count, Is.EqualTo(cachedRequests));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    [TestCase("close")]
    [TestCase("preference")]
    [TestCase("cost")]
    [TestCase("shutdown")]
    public async Task Operation_WhenCancelled_WaitsForAcknowledgementAndRejectsLateCompletionAcrossSettings(string trigger)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        Connection connection = new Connection(UpdateConnection.Unmetered);
        await using Harness ui = await Harness.OpenAsync(fixture, clock, connection, new UpdatePreferences(DownloadsEnabled: false));
        await ui.StartAsync();
        await ui.SaveAsync(UpdatePreferences.Default);
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (name, _) => Task.FromResult(name == ClientUpdateFixture.BundleName ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new LateStream(fixture.Signer.Package, reached, release))
        } : fixture.Respond(name));
        Task shutdown = Task.CompletedTask;
        try
        {
            // Act
            ui.Button("DownloadUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.PumpAsync();
            Assert.That(ui.Find<ProgressBar>("UpdateProgress").IsIndeterminate, Is.True);
            switch (trigger)
            {
                case "close":
                    ui.CloseSettings();
                    await ui.OpenSettingsAsync(drain: false);
                    break;
                case "preference":
                    await ui.SaveAsync(new UpdatePreferences(DownloadsEnabled: false), drain: false);
                    break;
                case "cost":
                    connection.Set(UpdateConnection.Unknown);
                    break;
                case "shutdown":
                    shutdown = ui.Service.DisposeAsync().AsTask();
                    break;
            }
            if (trigger == "preference")
            {
                await ui.PreferencesSavedAsync();
            }
            await ui.PumpAsync();

            // Assert
            Assert.That(ui.Service.IsBusy, Is.True);
            Assert.That(ui.Service.IsCancelling, Is.True);
            Assert.That(ui.Button("CheckForUpdates").IsEnabled, Is.False);
            Assert.That((await ui.Service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.PolicyBlocked));
            Assert.That((await ui.Service.DownloadAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Failure, Is.EqualTo(Failure.PolicyBlocked));
            Task cancelled = ui.Service.CancelAsync();
            Assert.That(cancelled.IsCompleted, Is.False);
            if (trigger == "shutdown")
            {
                Assert.That(shutdown.IsCompleted, Is.False);
            }
            release.TrySetResult();
            await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
            await ui.DrainAsync();
            await shutdown;
            Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Cancelled));
            Assert.That(ui.Service.IsBusy, Is.False);
            Assert.That(File.Exists(Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName)), Is.False);
            Assert.That(Directory.GetFiles(Path.Combine(fixture.Directory, "packages"), "*.partial"), Is.Empty);
            Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
            if (trigger != "shutdown")
            {
                connection.Set(UpdateConnection.Unmetered);
                await ui.SaveAsync(UpdatePreferences.Default);
                fixture.Response = null;
                await ui.ClickAsync("CheckForUpdates");
                await ui.ClickAsync("DownloadUpdate");
                await ui.PumpAsync();
                Assert.That(ui.Service.Current.Stage, Is.EqualTo(UpdateStage.Ready));
                Assert.That(ui.Find<TextBlock>("UpdatesAvailability").Text, Does.Contain("download"));
            }
        }
        finally
        {
            release.TrySetResult();
            await ui.DrainAsync();
            await shutdown;
        }
    }

    private sealed class Connection(UpdateConnection cost) : IUpdateConnection
    {
        public UpdateConnection Current { get; private set; } = cost;

        public event EventHandler? Changed;

        internal void Set(UpdateConnection value)
        {
            Current = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class LateStream(byte[] bytes, TaskCompletionSource reached, TaskCompletionSource release) : MemoryStream(bytes)
    {
        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                return await base.ReadAsync(buffer[..((int)Length / 2)], cancellationToken);
            }
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly MainWindow _main;
        private readonly MainWindowViewModel _model;
        private readonly DesktopIntegration _integration;
        private SettingsWindow _settings = null!;
        private bool _settingsClosed;

        private Harness(MainWindow main, MainWindowViewModel model, DesktopIntegration integration, SqliteProfile profile, ScheduledClientUpdates service)
        {
            _main = main;
            _model = model;
            _integration = integration;
            Profile = profile;
            Service = service;
        }

        internal SqliteProfile Profile { get; }
        internal ScheduledClientUpdates Service { get; }

        public async ValueTask DisposeAsync()
        {
            CloseSettings();
            await Service.DisposeAsync();
            await DrainAsync();
            _integration.Dispose();
            _main.Close();
            await _model.DisposeAsync();
            await Profile.DisposeAsync();
        }

        internal static async Task<Harness> OpenAsync(ClientUpdateFixture fixture, TimeProvider time, Connection connection, UpdatePreferences? initial = null)
        {
            SqliteProfile profile = await EngineFixture.OpenAsync(fixture.Directory + "-profile");
            if (initial is not null)
            {
                await profile.SaveDesktopPreferencesAsync(DesktopPreferences.Default with
                {
                    Updates = initial
                }, CancellationToken.None);
            }
            ScheduledClientUpdates service = fixture.CreateScheduled(profile, connection, time);
            IMailApplication application = Substitute.For<IMailApplication>();
            MainWindowViewModel model = new MainWindowViewModel(application);
            MainWindow main = new MainWindow
            {
                DataContext = model
            };
            DesktopIntegration integration = new DesktopIntegration(main, profile, Substitute.For<INewMailNotification>(), service);
            main.Configure(application, null, integration, updates: service);
            main.Show();
            Harness harness = new Harness(main, model, integration, profile, service);
            await harness.OpenSettingsAsync();
            return harness;
        }

        internal async Task StartAsync()
        {
            await Service.StartAsync(CancellationToken.None);
            await SettleAsync();
        }

        internal async Task SettleAsync()
        {
            await Service.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await PumpAsync();
        }

        internal void CloseSettings()
        {
            if (!_settingsClosed)
            {
                _settings.Close();
                _settingsClosed = true;
            }
        }

        internal async Task OpenSettingsAsync(bool drain = true)
        {
            _main.OpenSettings("Updates");
            _settings = _main.OwnedWindows.OfType<SettingsWindow>().Single();
            _settingsClosed = false;
            if (drain)
            {
                await DrainAsync();
            }
            await PumpAsync();
        }

        internal T Find<T>(string name) where T : Control
        {
            return _settings.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        }

        internal Button Button(string name)
        {
            return Find<Button>(name);
        }

        internal async Task ClickAsync(string name)
        {
            // Service completion can precede the queued Settings render; observe the actual control
            // before activating it, with a deadline rather than a timing sleep or relaxed assertion.
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!Button(name).IsEnabled && DateTimeOffset.UtcNow < deadline)
            {
                await PumpAsync();
            }
            Assert.That(Button(name).IsEnabled, Is.True, name + ": " + Service.Current + "; busy=" + Service.IsBusy);
            Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await DrainAsync();
            await PumpAsync();
        }

        internal async Task SaveAsync(UpdatePreferences preferences, bool drain = true)
        {
            Find<CheckBox>("UpdateChecksEnabled").IsChecked = preferences.ChecksEnabled;
            Find<CheckBox>("UpdateDownloadsEnabled").IsChecked = preferences.DownloadsEnabled;
            Find<CheckBox>("UpdateUnmeteredOnly").IsChecked = preferences.UnmeteredOnly;
            Find<ComboBox>("UpdateSchedule").SelectedIndex = preferences.CheckIntervalHours == 168 ? 1 : 0;
            Button("SaveUpdatePreferences").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            if (drain)
            {
                await DrainAsync();
            }
            await PumpAsync();
        }

        internal async Task PreferencesSavedAsync()
        {
            // Acquiring the integration's serial writer observes the preceding Settings save without awaiting the gated download.
            await _integration.ConfigureUpdatesAsync(new UpdatePreferences(DownloadsEnabled: false), CancellationToken.None);
            await PumpAsync();
        }

        internal async Task PumpAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        internal Task DrainAsync()
        {
            MethodInfo drain = typeof(SettingsWindow).Assembly.GetType("Postfold.UI.Controls.FormControls")!
                .GetMethod("DrainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            return ((Task)drain.Invoke(null, new object[] { _main })!).WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
}

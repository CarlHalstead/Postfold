using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Services;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class DesktopIntegrationTests
{
    [AvaloniaTest]
    public async Task ConfigureUpdatesAsync_WhenDesktopChoicesChange_PreservesDisabledUpdateChoicesAcrossReopen()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        using DesktopIntegration integration = new DesktopIntegration(new Window(), profile, Substitute.For<INewMailNotification>());
        UpdatePreferences selected = new UpdatePreferences(false, false, true)
        {
            CheckIntervalHours = 168
        };

        // Act
        Failure updated = await integration.ConfigureUpdatesAsync(selected, CancellationToken.None);
        Failure desktop = await integration.ConfigureAsync(new DesktopPreferences(false, false), CancellationToken.None);
        await profile.DisposeAsync();
        await using SqliteProfile reopened = (await SqliteProfile.OpenAsync(
            System.IO.Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;
        DesktopPreferences persisted = await reopened.ReadDesktopPreferencesAsync(CancellationToken.None);

        // Assert
        Assert.That(updated, Is.EqualTo(Failure.None));
        Assert.That(desktop, Is.EqualTo(Failure.None));
        Assert.That(persisted.Updates, Is.EqualTo(selected));
        Assert.That(persisted.TrayEnabled, Is.False);
        Assert.That(persisted.NotificationsEnabled, Is.False);
    }

    [Test]
    public void DesktopPreferences_WhenReadingEarlierJson_PreservesChoicesAndRestrictsDownloadsToUnmetered()
    {
        // Arrange
        string earlier = "{\"TrayEnabled\":true,\"NotificationsEnabled\":false}";

        // Act
        DesktopPreferences current = System.Text.Json.JsonSerializer.Deserialize<DesktopPreferences>(earlier)!;

        // Assert
        Assert.That(current.TrayEnabled, Is.True);
        Assert.That(current.NotificationsEnabled, Is.False);
        Assert.That(current.Updates.UnmeteredOnly, Is.True);
        Assert.That(current.Updates.CheckIntervalHours, Is.EqualTo(24));
    }

    [AvaloniaTest]
    public async Task ConfigureAsync_WhenNotificationsAreDisabled_PersistsPreferenceAndNeverCallsNativePort()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        INewMailNotification notifications = Substitute.For<INewMailNotification>();
        Window window = new Window();
        using DesktopIntegration integration = new DesktopIntegration(window, profile, notifications);
        Assert.That(await integration.ReadPreferencesAsync(CancellationToken.None), Is.EqualTo(DesktopPreferences.Default));

        // Act
        Failure configured = await integration.ConfigureAsync(new DesktopPreferences(false, false), CancellationToken.None);
        Failure notified = integration.NotifyNewMail();
        await profile.DisposeAsync();
        await using SqliteProfile reopened = (await SqliteProfile.OpenAsync(
            System.IO.Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;

        // Assert
        Assert.That(configured, Is.EqualTo(Failure.None));
        Assert.That(notified, Is.EqualTo(Failure.None));
        Assert.That(integration.TrayEnabled, Is.False);
        Assert.That(
            await reopened.ReadDesktopPreferencesAsync(CancellationToken.None),
            Is.EqualTo(new DesktopPreferences(false, false))
        );
        notifications.DidNotReceive().ShowGeneric();
    }

    [AvaloniaTest]
    public async Task ConfigureUpdatesAsync_WhenScheduleIsUnsupported_RejectsBeforeReadingOrWritingPreferences()
    {
        // Arrange
        IDesktopPreferenceStore store = Substitute.For<IDesktopPreferenceStore>();
        using DesktopIntegration integration = new DesktopIntegration(new Window(), store, Substitute.For<INewMailNotification>());

        // Act
        UpdatePreferences preferences = new UpdatePreferences()
        {
            CheckIntervalHours = 0
        };
        Failure result = await integration.ConfigureUpdatesAsync(preferences, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Validation));
        await store.DidNotReceiveWithAnyArgs().ReadDesktopPreferencesAsync(default);
        await store.DidNotReceiveWithAnyArgs().SaveDesktopPreferencesAsync(default!, default);
    }

    [AvaloniaTest]
    public async Task ConfigureAsync_WhenNativeTrayIsUnavailable_PreservesDefaultExitPreference()
    {
        // Arrange
        IDesktopPreferenceStore store = Substitute.For<IDesktopPreferenceStore>();
        INewMailNotification notification = Substitute.For<INewMailNotification>();
        using DesktopIntegration integration = new DesktopIntegration(new Window(), store, notification);

        // Act
        Failure configured = await integration.ConfigureAsync(new DesktopPreferences(true, true), CancellationToken.None);

        // Assert
        Assert.That(configured, Is.EqualTo(Failure.Unavailable));
        Assert.That(integration.TrayEnabled, Is.False);
        await store.DidNotReceiveWithAnyArgs().SaveDesktopPreferencesAsync(default!, default);
    }

    [AvaloniaTest]
    public void ShowGeneric_WhenNoNativeWindowExists_ReportsUnavailableWithoutCreatingNotification()
    {
        // Arrange
        using WindowsNewMailNotification notifications = new WindowsNewMailNotification(new Window(), TimeProvider.System);

        // Act
        Failure result = notifications.ShowGeneric();

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Unavailable));
    }
}

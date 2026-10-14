using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
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

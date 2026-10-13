using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using Postfold.Desktop.Services;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.HtmlReading;

public sealed class HtmlQualificationTests
{
    [AvaloniaTest]
    public async Task ReadAsync_WhenNativeQualificationIsMissing_RemainsPlainTextAndNeverLoadsContentOrImages()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        IRemoteImageFetcher fetcher = Substitute.For<IRemoteImageFetcher>();
        await using MainWindowViewModel model = new MainWindowViewModel(application);
        MainWindow window = new MainWindow
        {
            DataContext = model
        };
        await using WindowsHtmlReadingService reader = new WindowsHtmlReadingService(
            window,
            application,
            fetcher,
            "unused-synthetic-directory",
            qualified: false
        );
        window.ConfigureHtml(reader);
        MessageSnapshot message = new MessageSnapshot(
            new AccountId(1),
            new MessageId(1),
            new ProviderIdentity(new AccountId(1), "synthetic", null),
            "Synthetic",
            "",
            null,
            DateTimeOffset.UnixEpoch,
            100,
            false,
            false,
            ContentAvailability.Available,
            PendingState.None
        );

        // Act
        Failure rendered = await reader.ReadAsync(message, true, CancellationToken.None);
        Failure images = await reader.LoadImagesAsync(message, CancellationToken.None);

        // Assert
        Assert.That(rendered, Is.EqualTo(Failure.Unavailable));
        Assert.That(images, Is.EqualTo(Failure.Unavailable));
        Assert.That(window.FindControl<MenuItem>("HtmlButton")!.IsEnabled, Is.False);
        Assert.That(window.FindControl<MenuItem>("ImagesButton")!.IsEnabled, Is.False);
        Assert.That(window.FindControl<ScrollViewer>("PlainPreviewScroll")!.IsVisible, Is.True);
        Assert.That(window.FindControl<ContentControl>("HtmlSurface")!.Content, Is.Null);
        await application.DidNotReceiveWithAnyArgs().ReadHtmlAsync(default!, default, default);
        await fetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default, default);
    }
}

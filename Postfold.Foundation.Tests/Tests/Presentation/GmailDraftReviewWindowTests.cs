using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Drafts;
using Postfold.Desktop.Composition;
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

namespace Postfold.Foundation.Tests.Tests.Presentation;

public sealed class GmailDraftReviewWindowTests
{
    [AvaloniaTest]
    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task ReviewVersions_WhenPostponedOrExplicitlyCopied_PreservesHeldOriginalAndCreatesOnlyOnDecision(
        bool copy,
        bool dark
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow

        {
            DataContext = model,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            FontSize = dark ? 24 : 16
        };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            await main.DraftsAsync(held.Account);
            Window drafts = main.OwnedWindows.Single(item => item.Title == Strings.Drafts);
            drafts.GetVisualDescendants().OfType<ListBox>().Single().SelectedItem = held;

            // Act
            Click(drafts, "ReviewVersions");
            await FormControls.DrainAsync(main);
            Window review = main.OwnedWindows.Single(item => item.Title == Strings.Get("ReviewVersions"));
            review.Width = dark ? 360 : 760;
            Button create = Control<Button>(review, "CopyReviewedDraft");
            Assert.That(AutomationProperties.GetName(create), Is.EqualTo(Strings.Get("CopyReviewedDraft")));
            Assert.That(review.GetVisualDescendants().OfType<ComboBox>().Single().Items.Count, Is.GreaterThanOrEqualTo(3));
            if (copy)
            {
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await FormControls.DrainAsync(main);
                create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await FormControls.DrainAsync(main);
                Assert.That(Control<TextBlock>(review, "DraftReviewStatus").Text, Does.Contain(Strings.Get("ReviewedDraftCreated")));
            }
            Click(review, "PostponeDraftReview");
            await FormControls.DrainAsync(main);

            // Assert
            Assert.That(review.IsVisible, Is.False);
            Assert.That((await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(item => item.Id == held.Id), Is.EqualTo(held));
            Assert.That(await runtime.Catalog.OutboxAsync(held.Account, CancellationToken.None), Is.Empty);
            GmailDraftReviewFixture.AssertSafeEffects(provider, copy ? 2 : 1);
        }
        finally
        {
            Assert.That(await main.FlushComposeAsync(), Is.True);
            main.Close();
            await FormControls.DrainAsync(main);
        }
    }

    [AvaloniaTest]
    public async Task FlushComposeAsync_WhenReviewCreateIsActive_CancelsAndDrainsWithoutRepeatingCreate()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        await using MainWindowViewModel model = new MainWindowViewModel(runtime.Application);
        await model.InitialiseAsync();
        MainWindow main = new MainWindow

        {
            DataContext = model
        };
        main.Configure(runtime.Application, null);
        main.Show();
        try
        {
            await main.DraftsAsync(held.Account);
            Window drafts = main.OwnedWindows.Single(item => item.Title == Strings.Drafts);
            drafts.GetVisualDescendants().OfType<ListBox>().Single().SelectedItem = held;
            Click(drafts, "ReviewVersions");
            await FormControls.DrainAsync(main);
            Window review = main.OwnedWindows.Single(item => item.Title == Strings.Get("ReviewVersions"));
            provider.DraftEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(review, "CopyReviewedDraft");
            await provider.DraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            Assert.That(await main.FlushComposeAsync(), Is.True);
            main.Close();
            await FormControls.DrainAsync(main);

            // Assert
            DraftSnapshot copy = (await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(item => item.Id != held.Id);
            Assert.That(copy.Held, Is.True);
            Assert.That(await runtime.Application.PushDraftAsync(copy.Account, copy.Id, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
            GmailDraftReviewFixture.AssertSafeEffects(provider, 2);
        }
        finally
        {
            await main.FlushComposeAsync();
            main.Close();
            await FormControls.DrainAsync(main);
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

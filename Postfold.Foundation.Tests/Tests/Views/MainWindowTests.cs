using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.UI.Controls;
using Postfold.UI.Policies;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.ComponentModel;
using System.Linq;

namespace Postfold.Foundation.Tests.Tests.Views;

public sealed class MainWindowTests
{
    [AvaloniaTest]
    public void Reader_WhenSelectingText_AllowsCopyWithoutAnEditableTextBoxOrEmptyAttachmentPanel()
    {
        // Arrange
        MainWindow window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();
        try
        {
            SelectableTextBlock reader = window.FindControl<SelectableTextBlock>("PlainPreview")!;
            reader.Text = "A synthetic message for selection.";

            // Act
            reader.SelectionStart = 2;
            reader.SelectionEnd = 11;
            reader.Focus();
            window.KeyTextInput("attempted edit");

            // Assert
            Assert.That(reader.SelectedText, Is.EqualTo("synthetic"));
            Assert.That(reader.CanCopy, Is.True);
            Assert.That(reader.Text, Is.EqualTo("A synthetic message for selection."));
            Assert.That(window.FindControl<StackPanel>("ReaderAttachmentPanel")!.IsVisible, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public void Disclosure_WhenMotionPreferenceChanges_UsesNativeTransitionOrImmediateHeight(bool reduced)
    {
        // Arrange
        using ReducedMotionPolicy policy = new ReducedMotionPolicy(reduced);
        AnimatedDisclosure disclosure = new AnimatedDisclosure
        {
            Child = new Border { Width = 100, Height = 80 }
        };

        // Act
        disclosure.IsExpanded = true;
        disclosure.Measure(new Size(100, 100));

        // Assert
        Assert.That(disclosure.Progress, Is.EqualTo(1));
        Assert.That(disclosure.DesiredSize.Height, Is.EqualTo(80));
        Assert.That(disclosure.Transitions is null, Is.EqualTo(reduced));
        disclosure.IsExpanded = false;
        disclosure.Measure(new Size(100, 100));
        Assert.That(disclosure.DesiredSize.Height, Is.Zero);
        Assert.That(disclosure.IsHitTestVisible, Is.False);
    }

    [AvaloniaTest]
    [TestCase(14)]
    [TestCase(24)]
    public void ApplyAppearance_WhenWindowIsMinimumSize_PreservesReadableBody(double fontSize)
    {
        // Arrange
        MainWindow window = new MainWindow
        {
            Width = 760,
            Height = 480,
            DataContext = new MainWindowViewModel()
        };
        window.Show();
        try
        {
            // Act
            window.ApplyAppearance(Postfold.Core.Models.Appearance.AppearanceSettings.Default with
            {
                FontSize = fontSize,
                Preview = "Bottom"
            });
            window.UpdateLayout();

            // Assert
            Assert.That(window.FindControl<ScrollViewer>("PlainPreviewScroll")!.Bounds.Height, Is.GreaterThan(fontSize));
            Assert.That(window.FindControl<ListBox>("MessageList")!.Bounds.Height, Is.GreaterThan(fontSize));
            Assert.That(window.FindControl<TreeView>("ScopeList")!.Bounds.Height, Is.GreaterThan(fontSize));
            Assert.That(window.FindControl<Button>("ReplyButton")!.IsEffectivelyVisible, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ApplyAppearance_WhenThemeChangesThroughHighContrast_KeepsControlsAndTeardownValid()
    {
        // Arrange
        MainWindow window = new MainWindow
        {
            DataContext = new MainWindowViewModel()
        };
        window.Show();
        try
        {
            // Act
            Application.Current!.RequestedThemeVariant = MailTheme.HighContrast;
            window.UpdateLayout();
            window.ApplyAppearance(Postfold.Core.Models.Appearance.AppearanceSettings.Default with
            {
                Theme = "Light"
            });
            window.UpdateLayout();

            // Assert
            Assert.That(window.ActualThemeVariant, Is.EqualTo(Avalonia.Styling.ThemeVariant.Light));
            Assert.That(window.FindControl<Button>("ComposeButton")!.IsVisible, Is.True);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void CyclePaneFocus_WhenPreviewIsHidden_SkipsHiddenPaneAndCyclesBackwards()
    {
        // Arrange
        MainWindow window = new MainWindow
        {
            DataContext = new MainWindowViewModel()
        };
        window.Show();
        try
        {
            window.ApplyAppearance(Postfold.Core.Models.Appearance.AppearanceSettings.Default with
            {
                Preview = "Hidden"
            });
            TreeView navigation = window.FindControl<TreeView>("ScopeList")!;
            ListBox messages = window.FindControl<ListBox>("MessageList")!;
            navigation.Focus();

            // Act
            window.CyclePaneFocus(false);
            bool messageFocused = messages.IsKeyboardFocusWithin;
            window.CyclePaneFocus(true);

            // Assert
            Assert.That(messageFocused, Is.True);
            Assert.That(navigation.IsKeyboardFocusWithin, Is.True);
            Assert.That(window.FindControl<SelectableTextBlock>("PlainPreview")!.IsKeyboardFocusWithin, Is.False);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void MessageList_WhenTenThousandRowsAreLoaded_VirtualisesAndPreservesSelectedItemDuringScroll()
    {
        // Arrange
        ListBox list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, 10000).ToArray()
        };
        Window window = new Window
        {
            Width = 300,
            Height = 400,
            Content = list
        };
        window.Show();
        try
        {
            list.SelectedItem = 4;

            // Act
            list.ScrollIntoView(9000);
            window.UpdateLayout();

            // Assert
            Assert.That(list.ItemsPanelRoot, Is.TypeOf<VirtualizingStackPanel>());
            Assert.That(list.GetRealizedContainers().Count(), Is.LessThan(100));
            Assert.That(list.SelectedItem, Is.EqualTo(4));
            Assert.That(list.ContainerFromIndex(9000), Is.Not.Null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void ReducedMotion_WhenThemeSetsAControlTransition_RemovesAnimation()
    {
        // Arrange
        using ReducedMotionPolicy policy = new ReducedMotionPolicy(true);
        Button button = new Button();

        // Act
        button.Transitions = new Transitions
        {
            new DoubleTransition
{
Property = Button.OpacityProperty,
Duration = TimeSpan.FromSeconds(1)
}
        };

        // Assert
        Assert.That(button.Transitions, Is.Null);
    }

    [AvaloniaTest]
    public void Title_WhenViewModelChanges_UpdatesWindowAndContent()
    {
        // Arrange
        MainWindowViewModel viewModel = new MainWindowViewModel();
        MainWindow window = new MainWindow
        {
            DataContext = viewModel
        };
        window.Show();

        try
        {
            // Act
            viewModel.Title = "Foundation verification";

            // Assert
            Assert.That(window.Title, Is.EqualTo(viewModel.Title));
            Assert.That(window.FindControl<ListBox>("MessageList"), Is.Not.Null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void KeyTextInput_WhenTextBoxHasFocus_UpdatesText()
    {
        // Arrange
        TextBox textBox = new TextBox();
        Window window = new Window
        {
            Content = textBox
        };
        window.Show();

        try
        {
            textBox.Focus();

            // Act
            window.KeyTextInput("Synthetic input");

            // Assert
            Assert.That(textBox.Text, Is.EqualTo("Synthetic input"));
        }
        finally
        {
            window.Close();
        }
    }

    [Test]
    public void Title_WhenAssignedSameValue_DoesNotNotifyAgain()
    {
        // Arrange
        MainWindowViewModel viewModel = new MainWindowViewModel();
        PropertyChangedEventHandler observer = Substitute.For<PropertyChangedEventHandler>();
        viewModel.PropertyChanged += observer;

        // Act
        viewModel.Title = "Changed title";
        viewModel.Title = "Changed title";

        // Assert
        Assert.That(viewModel.Title, Is.EqualTo("Changed title"));
        observer.Received(1).Invoke(viewModel, Arg.Is<PropertyChangedEventArgs>(args => args.PropertyName == "Title"));
    }
}

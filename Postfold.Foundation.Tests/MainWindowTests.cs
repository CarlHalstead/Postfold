using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.UI;

namespace Postfold.Foundation.Tests;

public sealed class MainWindowTests
{
    [AvaloniaTest]
    public void CyclePaneFocus_WhenPreviewIsHidden_SkipsHiddenPaneAndCyclesBackwards()
    {
        // Arrange
        MainWindow window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();
        try
        {
            window.ApplyAppearance(Postfold.Core.AppearanceSettings.Default with
            {
                Preview = "Hidden"
            });
            ListBox navigation = window.FindControl<ListBox>("ScopeList")!;
            ListBox messages = window.FindControl<ListBox>("MessageList")!;
            navigation.Focus();

            // Act
            window.CyclePaneFocus(false);
            bool messageFocused = messages.IsKeyboardFocusWithin;
            window.CyclePaneFocus(true);

            // Assert
            Assert.That(messageFocused, Is.True);
            Assert.That(navigation.IsKeyboardFocusWithin, Is.True);
            Assert.That(window.FindControl<TextBox>("PlainPreview")!.IsKeyboardFocusWithin, Is.False);
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
        ListBox list = new ListBox { ItemsSource = Enumerable.Range(0, 10000).ToArray() };
        Window window = new Window { Width = 300, Height = 400, Content = list };
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
        using ReducedMotionPolicy policy = new ReducedMotionPolicy();
        Button button = new Button();

        // Act
        button.Transitions = new Transitions
        {
            new DoubleTransition { Property = Button.OpacityProperty, Duration = TimeSpan.FromSeconds(1) }
        };

        // Assert
        Assert.That(button.Transitions, Is.Null);
    }

    [AvaloniaTest]
    public void Title_WhenViewModelChanges_UpdatesWindowAndContent()
    {
        // Arrange
        MainWindowViewModel viewModel = new MainWindowViewModel();
        MainWindow window = new MainWindow { DataContext = viewModel };
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
        Window window = new Window { Content = textBox };
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

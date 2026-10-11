using System.ComponentModel;
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
            Assert.That(((TextBlock)window.Content!).Text, Is.EqualTo(viewModel.Title));
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

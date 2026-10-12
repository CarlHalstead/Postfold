using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Desktop;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class DesktopCompositionTests
{
    [AvaloniaTest]
    public void Initialize_WhenDesktopLifetimeIsAvailable_ComposesMainWindow()
    {
        // Arrange
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        App application = new App
        {
            ApplicationLifetime = lifetime
        };

        // Act
        application.OnFrameworkInitializationCompleted();
        MainWindow window = (MainWindow)lifetime.MainWindow!;
        window.Show();

        try
        {
            // Assert
            Assert.That(window.DataContext, Is.TypeOf<MainWindowViewModel>());
            Assert.That(window.Title, Is.EqualTo(((MainWindowViewModel)window.DataContext!).Title));
            Assert.That(window.IsVisible, Is.True);
        }
        finally
        {
            window.Close();
        }

        Assert.That(window.IsVisible, Is.False);
    }
}

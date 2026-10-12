using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Postfold.UI;

namespace Postfold.Desktop;

public sealed class App : Application
{
    private ReducedMotionPolicy? _motion;

    public override void Initialize()
    {
        _motion = new ReducedMotionPolicy();
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += (_, _) => _motion?.Dispose();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
            if (Program.StartClient)
            {
                _ = new DesktopSession(desktop, (MainWindow)desktop.MainWindow);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Diagnostics;
using Postfold.UI.Policies;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;

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
            if (desktop.Args is ["--qualify-reader", string readerOutput])
            {
                desktop.MainWindow = ReaderQualification.Create(readerOutput);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (desktop.Args is ["--qualify-renderer", string qualificationOutput])
            {
                desktop.MainWindow = RendererQualification.Create(qualificationOutput);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            if (desktop.Args is ["--renderer-probe" or "--renderer-security-probe", string output])
            {
                desktop.MainWindow = RendererProbe.Create(output, desktop.Args[0] == "--renderer-security-probe");
                base.OnFrameworkInitializationCompleted();
                return;
            }

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

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Postfold.Desktop.Composition;
using Postfold.UI.Policies;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;

namespace Postfold.Desktop;

public sealed class App : Application
{
    private ReducedMotionPolicy? _motion;

    /// <summary>Overrides the motion preference; null uses the operating system setting.</summary>
    public bool? ReduceMotion
    {
        get;
        init;
    }

    public override void Initialize()
    {
        _motion = new ReducedMotionPolicy(ReduceMotion);
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Postfold.UI/"))
        {
            Source = new Uri("avares://Postfold.UI/Resources/MailStyles.axaml")
        });
        MailTheme.Install(this);
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

using Avalonia;
using System;
using System.Linq;

namespace Postfold.Desktop;

public static class Program
{
    internal static bool StartClient
    {
        get;
        private set;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal)))
        {
            return;
        }
        StartClient = true;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect();
    }
}

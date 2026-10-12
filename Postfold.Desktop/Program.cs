using System;
using System.Diagnostics;
using Avalonia;

namespace Postfold.Desktop;

public static class Program
{
    internal static long Started
    {
        get; private set;
    }
    internal static bool StartClient
    {
        get;
        private set;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        StartClient = true;
        Started = Stopwatch.GetTimestamp();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect();
    }
}

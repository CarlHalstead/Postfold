using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        string? probeOutput = args is ["--renderer-probe" or "--renderer-security-probe", string output] ? output : null;
        if (probeOutput is not null)
        {
            File.WriteAllText(probeOutput, $"Probe entered Main\nRuntime: {RuntimeInformation.FrameworkDescription}\n" +
                $"OS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n");
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception) when (probeOutput is not null)
        {
            File.AppendAllText(probeOutput, $"Startup failure: {exception.GetType().FullName}: {exception.Message}\n");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect();
    }
}

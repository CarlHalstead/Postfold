using Avalonia;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

public static class QualificationProgram
{
    internal static long Started { get; private set; }
    internal static bool StartClient { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--qualify-installed-update", string mode, string specification])
        {
            InstalledUpdateQualification.Run(mode, specification);
            return;
        }
        if (args is ["--update-trust-worker", .. string[] worker])
        {
            UpdateTrustProcess.Run(worker);
            return;
        }
        StartClient = true;
        Started = Stopwatch.GetTimestamp();
        string? probeOutput = args is ["--renderer-probe" or "--renderer-security-probe" or "--qualify-renderer" or
            "--qualify-reader", string output] ? output : null;
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
        return AppBuilder.Configure<QualificationApp>().UsePlatformDetect();
    }
}

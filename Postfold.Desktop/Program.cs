using Avalonia;
using System;
using System.IO;
using System.Linq;
using Velopack;

namespace Postfold.Desktop;

public static class Program
{
    internal static bool StartClient { get; private set; }

    internal static string? ProfileDirectory { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();
        if (!TryGetLaunchArguments(args, out string? profileDirectory, out string[] activationArguments))
        {
            return;
        }
        ProfileDirectory = profileDirectory;
        StartClient = true;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(activationArguments);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect();
    }

    internal static bool TryGetLaunchArguments(
        string[] args,
        out string? profileDirectory,
        out string[] activationArguments
    )
    {
        profileDirectory = null;
        activationArguments = args;
        if (args is ["--profile", string directory, .. string[] remaining])
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                return false;
            }
            profileDirectory = Path.GetFullPath(directory);
            activationArguments = remaining;
        }
        return !activationArguments.Any(argument => argument.StartsWith("--", StringComparison.Ordinal));
    }
}

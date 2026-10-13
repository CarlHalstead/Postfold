using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Postfold.Desktop.Policies.HtmlReading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

/// <summary>Development-only native feasibility probe; never accepts message content or URLs.</summary>
public static class RendererProbe
{
    public static Window Create(string output, bool secured = false)
    {
        NativeWebView view = new NativeWebView();
        WindowsHtmlIsolation? isolation = null;
        CancellationTokenSource lifetime = new CancellationTokenSource();
        bool closed = false;
        view.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                string directory = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(output))!,
                    "renderer-probe-data",
                    Guid.NewGuid().ToString("N")
                );
                Directory.CreateDirectory(directory);
                windows.UserDataFolder = directory;
                windows.IsInPrivateModeEnabled = true;
                windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                File.AppendAllText(output, "Explicit isolated probe data directory configured.\n");
            }
        };
        Window window = new Window
        {
            Title = "Postfold renderer feasibility",
            Width = 600,
            Height = 400,
            Content = view
        };
        view.AdapterCreated += async (_, _) =>
        {
            object? handle = view.TryGetPlatformHandle();
            string surface = string.Join("\n", typeof(NativeWebView).GetEvents().Select(item => item.Name));
            File.AppendAllText(output, $"Adapter: {view.AdapterInfo}\nHandle: {handle?.GetType().FullName}\nEvents:\n{surface}\n");
            if (secured)
            {
                if (!OperatingSystem.IsWindows() || handle is not IWindowsWebView2PlatformHandle windows)
                {
                    File.AppendAllText(output, "Isolation unsupported; no hostile document loaded.\n");
                    window.Close();
                    return;
                }

                isolation = new WindowsHtmlIsolation(windows);
                isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    bool completed = await isolation.NavigateDocumentAsync(
                        "<!doctype html><title>Postfold synthetic isolation probe</title>" +
                    "<script>globalThis.postfoldAttackExecuted=true;chrome.webview.postMessage('synthetic')</script>" +
                    "<img src='http://127.0.0.1:9/synthetic-pixel' onerror='globalThis.postfoldAttackExecuted=true'>" +
                    "<p>Authored synthetic native isolation fixture</p>",
                        deadline.Token
                    );
                    File.AppendAllText(output, $"Native navigation completed: {completed}\n" +
                        $"Native navigation error: {isolation.LastNavigationError}\n");
                    if (completed)
                    {
                        string executed = await isolation.InspectSyntheticScriptAsync(
                            "globalThis.postfoldAttackExecuted === true"
                        ).WaitAsync(deadline.Token);
                        File.AppendAllText(output, $"Attack executed: {executed}\n");
                    }
                }
                catch (OperationCanceledException)
                {
                    File.AppendAllText(output, "Native navigation/inspection deadline reached.\n");
                }

                if (closed)
                {
                    return;
                }

                File.AppendAllText(output, $"Scripts disabled: {isolation.ScriptsDisabled}\n" +
                    $"Bridges disabled: {isolation.BridgesDisabled}\n" +
                    $"Native navigation starts: {isolation.StartedNavigations}\n" +
                    $"Authored documents served: {isolation.ServedDocuments}\n" +
                    $"Denied resources: {isolation.DeniedResources}\n" +
                    $"Denied navigations: {isolation.DeniedNavigations}\n" +
                    $"Last denied navigation: {isolation.LastDeniedNavigation}\n" +
                    "This narrow probe does not qualify the full H01-H04 matrix.\n");
                Dispatcher.UIThread.Post(window.Close);
            }
            else
            {
                view.NavigateToString("<!doctype html><title>Postfold synthetic native probe</title><p>Native feasibility</p>");
            }
        };
        view.NavigationCompleted += (_, args) =>
        {
            File.AppendAllText(output, $"Navigation completed: {args.IsSuccess}\n");
            if (secured && OperatingSystem.IsWindows() && isolation is not null)
            {
                File.AppendAllText(output, $"Denied navigations: {isolation.DeniedNavigations}\n" +
                    $"Last denied navigation: {isolation.LastDeniedNavigation}\n");
            }
            if (!secured)
            {
                Dispatcher.UIThread.Post(window.Close);
            }
        };
        DispatcherTimer timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            File.AppendAllText(output, "Probe deadline reached.\n");
            window.Close();
        };
        window.Opened += (_, _) => timer.Start();
        window.Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            timer.Stop();
            if (OperatingSystem.IsWindows())
            {
                isolation?.Dispose();
            }
        };
        return window;
    }
}

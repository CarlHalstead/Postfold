using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Postfold.Core;
using Postfold.UI;

namespace Postfold.Desktop;

/// <summary>Authored native security cases, independent of all real profiles.</summary>
public static class RendererQualification
{
    public static Window Create(string output)
    {
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
            "renderer-qualification-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        NativeWebView view = new NativeWebView();
        WindowsHtmlNetworkIsolation network = new WindowsHtmlNetworkIsolation();
        Window window = new Window { Title = "Postfold synthetic renderer qualification", Width = 800, Height = 600, Content = view };
        CancellationTokenSource lifetime = new CancellationTokenSource();
        TaskCompletionSource destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<object> results = [];
        bool running = false;
        view.EnvironmentRequested += (_, args) =>
        {
            args.EnableDevTools = false;
            if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            {
                windows.UserDataFolder = directory;
                windows.IsInPrivateModeEnabled = true;
                windows.AllowSingleSignOnUsingOSPrimaryAccount = false;
                windows.AdditionalBrowserArguments = network.Arguments;
            }
        };
        view.AdapterDestroyed += (_, _) =>
        {
            network.Dispose();
            destroyed.TrySetResult();
        };
        view.AdapterCreated += async (_, _) =>
        {
            if (running)
            {
                return;
            }

            running = true;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                if (!OperatingSystem.IsWindows() || view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
                {
                    throw new PlatformNotSupportedException("Windows WebView2 isolation is required.");
                }

                using WindowsHtmlIsolation isolation = new WindowsHtmlIsolation(handle);
                await RunAsync(isolation, results, deadline.Token);
            }
            catch (Exception exception)
            {
                results.Add(new
                {
                    Case = "harness",
                    Passed = false,
                    Error = exception.GetType().Name
                });
            }
            finally
            {
                window.Content = null;
                try
                {
                    await destroyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    results.Add(new
                    {
                        Case = "native-adapter-destroyed-before-window-close",
                        Passed = true
                    });
                }
                catch (TimeoutException)
                {
                    results.Add(new
                    {
                        Case = "native-adapter-destroyed-before-window-close",
                        Passed = false
                    });
                }

                object report = new
                {
                    CapturedUtc = DateTimeOffset.UtcNow,
                    Runtime = RuntimeInformation.FrameworkDescription,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    LogicalProcessors = Environment.ProcessorCount,
                    ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                    RuntimeAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    Adapter = view.AdapterInfo?.ToString(),
                    Cases = results,
                    RealProfilesUsed = false,
                    AcceptanceComplete = false,
                    Remaining = new[]
                    {
                        "Case-by-case review of all recorded native results",
                        "Actual reader cases and performance recorded separately by --qualify-reader",
                        "Native reader screen-reader/focus qualification",
                        "Renderer process-tree performance and shutdown stderr review"
                    }
                };
                await File.WriteAllTextAsync(output,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                if (window.IsVisible)
                {
                    window.Close();
                }
            }
        };
        window.Closed += (_, _) => lifetime.Cancel();
        return window;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunAsync(WindowsHtmlIsolation isolation, List<object> results, CancellationToken cancellationToken)
    {
        TcpListener sentinel = new TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        int contacts = 0;
        using CancellationTokenSource listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task observer = ObserveAsync();
        string remote = "http://127.0.0.1:" + ((IPEndPoint)sentinel.LocalEndpoint).Port + "/synthetic";
        byte[] pixel = QualificationImage.CreatePixel();
        try
        {
            await DocumentAsync("inline-script-event-svg-bridge", "<script>globalThis.attack=true;" +
                "chrome.webview.postMessage('synthetic')</script><svg onload='globalThis.attack=true'></svg>" +
                "<img src='" + remote + "' onerror='globalThis.attack=true'>",
                "globalThis.attack !== true");
            await DocumentAsync("native-web-message-bridge-denial", "<p>Authored bridge fixture</p>",
                "(() => { try { chrome.webview.postMessage('authored'); } catch {} return true; })()");
            results.Add(new
            {
                Case = "no-native-web-messages-received",
                Passed = isolation.ReceivedWebMessages == 0
            });

            await DocumentAsync("remote-image-css-frame-denial", "<style>body{background:url('" + remote + "')}</style>" +
                "<img id='remote' src='" + remote + "'><iframe src='" + remote + "'></iframe>",
                "document.getElementById('remote').naturalWidth === 0");
            await DocumentAsync("object-embed-font-srcset-prefetch-denial",
                "<object data='" + remote + "'></object><embed src='" + remote + "'>" +
                "<link rel='prefetch' href='" + remote + "'><link rel='dns-prefetch' href='//localhost'>" +
                "<style>@font-face{font-family:attack;src:url('" + remote + "')}" +
                "body{font-family:attack}</style><img id='remote' srcset='" + remote + " 1x'>",
                "document.getElementById('remote').naturalWidth === 0 && getComputedStyle(document.body).fontFamily !== 'attack'");
            await DenialAsync("refresh-base-navigation-denial",
                "<base href='" + remote + "'><meta http-equiv='refresh' content='0;url=" + remote + "'>",
                "location.href='" + remote + "'", ["navigation"]);
            await DocumentAsync("csp-fetch-websocket-denial", "<p>Authored network fixture</p>",
                "(() => { fetch('" + remote + "').catch(() => {}); " +
                "try { new WebSocket('" + remote.Replace("http:", "ws:", StringComparison.Ordinal) +
                "'); } catch {} return true; })()");

            await DocumentAsync("javascript-url-denial", "<a id='action' href='javascript:globalThis.attack=true'>Synthetic</a>",
                "document.getElementById('action').click(); globalThis.attack !== true");
            string[] navigationTargets = [remote, "file:///C:/postfold-authored-nonexistent.txt",
                "data:text/html,synthetic", "about:blank", "postfold-authored:blocked"];
            foreach (string target in navigationTargets)
            {
                await DenialAsync("external-navigation-" + new Uri(target).Scheme,
                    "<a id='action' href='" + target + "'>Synthetic link</a>",
                    "document.getElementById('action').click()", ["navigation"]);
            }

            await DenialAsync("form-submit", "<form id='action' action='" + remote + "' method='post'></form>",
                "document.getElementById('action').submit()", ["navigation"]);
            await DenialAsync("popup", "<p>Synthetic popup target</p>", "window.open('" + remote + "')", ["popup"]);
            await DenialAsync("download", "<a id='action' download='synthetic.txt' href='" + remote + "'>Download</a>",
                "document.getElementById('action').click()", ["navigation", "download"]);
            await DenialAsync("notification-permission", "<p>Synthetic permission request</p>",
                "Notification.requestPermission()", ["permission"]);

            Guid selected = Guid.NewGuid();
            isolation.BeginDocument(selected, new Dictionary<string, byte[]> { ["pixel"] = pixel });
            bool cidLoaded = await isolation.NavigateDocumentAsync("<img id='cid' src='https://postfold.invalid/" +
                selected.ToString("N") + "/pixel'>", cancellationToken);
            results.Add(new
            {
                Case = "selected-context-image",
                Passed = cidLoaded && await InspectAsync("document.getElementById('cid').naturalWidth === 1")
            });
            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            bool staleLoaded = await isolation.NavigateDocumentAsync("<img id='cid' src='https://postfold.invalid/" +
                selected.ToString("N") + "/pixel'>", cancellationToken);
            results.Add(new
            {
                Case = "previous-context-image-denied",
                Passed = staleLoaded && await InspectAsync("document.getElementById('cid').naturalWidth === 0")
            });

            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int startedBefore = isolation.StartedNavigations;
            bool cancelledAsExpected = false;
            try
            {
                await isolation.NavigateDocumentAsync("<p>Must not navigate</p>", cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                cancelledAsExpected = true;
            }

            results.Add(new
            {
                Case = "pre-cancelled-navigation",
                Passed = cancelledAsExpected && isolation.StartedNavigations == startedBefore
            });
            Task<bool> stale = isolation.NavigateDocumentAsync("<p>Superseded document</p>", cancellationToken);
            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            bool superseded = false;
            try
            {
                await stale;
            }
            catch (OperationCanceledException)
            {
                superseded = true;
            }
            results.Add(new
            {
                Case = "superseded-navigation-cancelled",
                Passed = superseded
            });

            bool oversizedRejected = false;
            try
            {
                isolation.NavigateDocument(new string('x', 262145));
            }
            catch (ArgumentException)
            {
                oversizedRejected = true;
            }
            results.Add(new
            {
                Case = "oversized-document-rejected",
                Passed = oversizedRejected
            });

            Guid bounded = Guid.NewGuid();
            Dictionary<string, byte[]> large = new Dictionary<string, byte[]>();
            for (int index = 0; index < 4; index++)
            {
                large.Add("image" + index, new byte[4194304]);
            }
            isolation.BeginDocument(bounded, large);
            int deniedBefore = isolation.DeniedResources;
            string images = string.Concat(large.Keys.Select(key => "<img src='https://postfold.invalid/" + bounded.ToString("N") + "/" + key + "'>"));
            bool boundedLoaded = await isolation.NavigateDocumentAsync(images, cancellationToken);
            results.Add(new
            {
                Case = "native-response-budget",
                Passed = boundedLoaded && isolation.RetainedResponseBytes <= 16777216 &&
                    isolation.RetainedResponses <= 128 && isolation.DeniedResources > deniedBefore,
                Bytes = isolation.RetainedResponseBytes,
                Responses = isolation.RetainedResponses
            });
            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            results.Add(new
            {
                Case = "context-reset-releases-responses",
                Passed = isolation.RetainedResponses == 0 && isolation.RetainedResponseBytes == 0
            });

            Guid sanitizedContext = Guid.NewGuid();
            Outcome<HtmlRenderPlan> sanitized = HtmlPolicy.Plan("<script>globalThis.attack=true</script><form></form>" +
                "<object data='" + remote + "'></object><embed src='" + remote + "'><iframe src='" + remote + "'></iframe>" +
                "<base href='" + remote + "'><meta http-equiv='refresh' content='0;url=" + remote + "'>" +
                "<link rel='stylesheet' href='" + remote + "'><svg onload='globalThis.attack=true'></svg>" +
                "<a href='jav&#x61;script:globalThis.attack=true'>Inert link</a>" +
                "<img src='" + remote + "' srcset='" + remote + " 1x'><p>Readable synthetic content</p>",
                sanitizedContext, new Dictionary<string, string>());
            isolation.BeginDocument(sanitizedContext, new Dictionary<string, byte[]>());
            bool sanitizedLoaded = sanitized.IsSuccess && await isolation.NavigateDocumentAsync(sanitized.Value!.Html, cancellationToken);
            results.Add(new
            {
                Case = "parser-and-csp-native-document",
                Passed = sanitizedLoaded && await InspectAsync("document.querySelectorAll('script,form,iframe,object,embed,base,link,svg,a').length === 0 && " +
                    "document.querySelector('meta[http-equiv=\"Content-Security-Policy\"]') !== null && " +
                    "document.body.innerText.includes('Readable synthetic content')")
            });
            results.Add(new
            {
                Case = "native-settings",
                Passed = isolation.ScriptsDisabled && isolation.BridgesDisabled
            });
        }
        finally
        {
            await listening.CancelAsync();
            sentinel.Stop();
            await observer;
            results.Add(new
            {
                Case = "no-loopback-network-contacts",
                Passed = contacts == 0,
                Contacts = contacts
            });
        }

        async Task ObserveAsync()
        {
            try
            {
                while (!listening.IsCancellationRequested)
                {
                    using TcpClient client = await sentinel.AcceptTcpClientAsync(listening.Token);
                    contacts++;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        async Task<bool> InspectAsync(string expression)
        {
            return await isolation.InspectSyntheticScriptAsync(expression).WaitAsync(cancellationToken) == "true";
        }

        async Task DocumentAsync(string name, string html, string assertion)
        {
            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            int contactsBefore = contacts;
            bool navigated = await isolation.NavigateDocumentAsync(html, cancellationToken);
            results.Add(new
            {
                Case = name,
                Passed = navigated && await InspectAsync(assertion),
                Contacts = contacts - contactsBefore
            });
        }

        async Task DenialAsync(string name, string html, string action, string[] reasons)
        {
            isolation.BeginDocument(Guid.NewGuid(), new Dictionary<string, byte[]>());
            int contactsBefore = contacts;
            bool navigated = await isolation.NavigateDocumentAsync(
                "<p id='qualification-origin'>Authored protected document</p>" + html, cancellationToken);
            string source = await isolation.InspectSyntheticScriptAsync("location.href").WaitAsync(cancellationToken);
            TaskCompletionSource<string> denied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveDenial(string reason)
            {
                if (reasons.Contains(reason, StringComparer.Ordinal))
                {
                    denied.TrySetResult(reason);
                }
            }

            isolation.DenialObserved += ObserveDenial;
            string? denial = null;
            try
            {
                if (navigated)
                {
                    await isolation.InspectSyntheticScriptAsync(action).WaitAsync(cancellationToken);
                    denial = await denied.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
            catch (TimeoutException)
            {
                if (reasons.Contains("navigation", StringComparer.Ordinal) &&
                    await isolation.InspectSyntheticScriptAsync("location.href").WaitAsync(cancellationToken) == source &&
                    await InspectAsync("document.getElementById('qualification-origin') !== null"))
                {
                    denial = "browser-or-csp-prevented-navigation";
                }
            }
            finally
            {
                isolation.DenialObserved -= ObserveDenial;
            }

            results.Add(new
            {
                Case = name,
                Passed = navigated && denial is not null,
                Denial = denial,
                Contacts = contacts - contactsBefore
            });
        }
    }
}

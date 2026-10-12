using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Postfold.Core.Enums;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Diagnostics;

internal static class NativeReaderQualification
{
    [SupportedOSPlatform("windows")]
    internal static async Task RunAsync(
        Window owner,
        string directory,
        List<object> results,
        CancellationToken cancellationToken
    )
    {
        EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            Path.Combine(directory, "reader-profile"),
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            cancellationToken
        )).Value!;
        bool populated = (await host.Catalog.AccountsAsync(cancellationToken)).IsEmpty;
        await host.SeedSyntheticAsync(100000, cancellationToken);
        await host.SeedReadingFixtureAsync(cancellationToken);
        await using MainWindowViewModel model = new MainWindowViewModel(host.Application);
        await model.InitialiseAsync();
        MessageSnapshot message = model.Messages[0];
        model.Selected = message;
        MainWindow window = new MainWindow
        {
            DataContext = model
        };
        window.Configure(host.Application, null);
        window.Show(owner);
        using QualificationImageTransport transport = new QualificationImageTransport();
        WindowsHtmlReadingService reader = new WindowsHtmlReadingService(
            window,
            host.Application,
            new RemoteImageFetcher(transport),
            Path.Combine(directory, "reader-browser"),
            qualified: true
        );
        window.ConfigureHtml(reader);
        TaskCompletionSource destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            long firstStarted = Stopwatch.GetTimestamp();
            Failure read = await reader.ReadAsync(message, false, cancellationToken);
            double firstMilliseconds = Stopwatch.GetElapsedTime(firstStarted).TotalMilliseconds;
            NativeWebView? view = window.FindControl<ContentControl>("HtmlSurface")!.Content as NativeWebView;
            if (view is null || view.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
            {
                results.Add(new
                {
                    Case = "actual-reader-construction",
                    Passed = false,
                    Failure = read.ToString()
                });
                return;
            }

            view.AdapterDestroyed += (_, _) => destroyed.TrySetResult();
            CoreWebView2 core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
            results.Add(new
            {
                Case = "actual-reader-default-block-and-selected-cid",
                Passed = read == Failure.None && transport.Requests == 0 && await InspectAsync(
                    "document.images.length === 2 && document.images[0].naturalWidth === 1 && " +
                    "document.images[1].naturalWidth === 0 && globalThis.attack !== true"
                )
            });
            results.Add(new
            {
                Case = "actual-reader-host-font-applied",
                Passed = await InspectAsync("getComputedStyle(document.body).fontSize === '14px'")
            });
            window.FontSize = 28;
            results.Add(new
            {
                Case = "actual-reader-host-font-change-applied",
                Passed = await InspectAsync("getComputedStyle(document.body).fontSize === '28px'")
            });
            window.FontSize = 14;
            Failure consent = await reader.LoadImagesAsync(message, cancellationToken);
            results.Add(new
            {
                Case = "actual-reader-explicit-consent-through-fetch-pipeline",
                Passed = consent == Failure.None && transport.Requests == 1 && await InspectAsync(
                    "document.images[0].naturalWidth === 1 && document.images[1].naturalWidth === 1"
                )
            });

            model.Selected = model.Messages[1];
            int requestsBefore = transport.Requests;
            Failure staleConsent = await reader.LoadImagesAsync(message, cancellationToken);
            results.Add(new
            {
                Case = "actual-reader-selection-revokes-consent",
                Passed = staleConsent == Failure.NeedsReview && transport.Requests == requestsBefore &&
                    !window.FindControl<ContentControl>("HtmlSurface")!.IsVisible
            });
            model.Selected = message;
            await reader.ReadAsync(message, false, cancellationToken);
            results.Add(new
            {
                Case = "actual-reader-revisit-requires-new-consent",
                Passed = transport.Requests == requestsBefore && await InspectAsync("document.images[1].naturalWidth === 0")
            });
            transport.RedirectToPrivateAddress = true;
            requestsBefore = transport.Requests;
            Failure privateRedirect = await reader.LoadImagesAsync(message, cancellationToken);
            results.Add(new
            {
                Case = "actual-reader-private-redirect-denied",
                Passed = privateRedirect == Failure.Validation && transport.Requests == requestsBefore + 1 &&
                    await InspectAsync("document.images[1].naturalWidth === 0")
            });
            transport.RedirectToPrivateAddress = false;
            transport.Oversized = true;
            Failure oversized = await reader.LoadImagesAsync(message, cancellationToken);
            results.Add(new
            {
                Case = "actual-reader-oversized-image-denied",
                Passed = oversized == Failure.Validation && await InspectAsync("document.images[1].naturalWidth === 0")
            });
            transport.Oversized = false;

            List<double> previews = [];
            foreach (MessageSnapshot sample in model.Messages.Take(20).ToArray())
            {
                long started = Stopwatch.GetTimestamp();
                model.Selected = sample;
                Failure result = await reader.ReadAsync(sample, false, cancellationToken);
                await FrameAsync();
                if (result != Failure.None)
                {
                    results.Add(new
                    {
                        Case = "actual-reader-steady-preview",
                        Passed = false,
                        Failure = result.ToString()
                    });
                    break;
                }

                previews.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            model.Selected = message;
            await reader.ReadAsync(message, false, cancellationToken);
            ProcessTreeMeasurement.Snapshot memory = ProcessTreeMeasurement.Capture();
            double? p95 = previews.Count == 0 ? null : previews.Order().ElementAt((int)Math.Ceiling(previews.Count * 0.95) - 1);
            ProcessTreeMeasurement.Snapshot cpuBefore = ProcessTreeMeasurement.Capture();
            long idleStarted = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            ProcessTreeMeasurement.Snapshot cpuAfter = ProcessTreeMeasurement.Capture();
            double idleSeconds = Stopwatch.GetElapsedTime(idleStarted).TotalSeconds;
            bool stableTree = cpuBefore.Complete && cpuAfter.Complete &&
                cpuBefore.ProcessIds.Order().SequenceEqual(cpuAfter.ProcessIds.Order());
            double? cpu = stableTree ? Math.Max(0, cpuAfter.CpuSeconds - cpuBefore.CpuSeconds) /
                idleSeconds / Environment.ProcessorCount * 100 : null;
            results.Add(new
            {
                Case = "actual-reader-performance",
                IncludesInitialPopulation = populated,
                Passed = previews.Count == 20 && p95 <= 250 && memory.Complete && memory.PrivateBytes <= 419430400 && cpu < 1,
                FirstRendererMilliseconds = firstMilliseconds,
                PreviewSamples = previews,
                PreviewP95Milliseconds = p95,
                ProcessTreePrivateBytes = memory.PrivateBytes,
                ProcessIds = memory.ProcessIds,
                MemoryComplete = memory.Complete,
                IdleObservationSeconds = idleSeconds,
                IdleProcessTreeCpuPercent = cpu,
                IdleProcessTreeStable = stableTree,
                Accounts = model.Accounts.Count,
                CachedMessages = model.CachedMessageCount,
                WindowWidth = window.Bounds.Width,
                WindowHeight = window.Bounds.Height,
                Scaling = window.RenderScaling,
                Theme = window.ActualThemeVariant.ToString(),
                FontSize = window.FontSize,
                MemoryScope = "Application and all descendants, including any previous renderer processes still alive"
            });

            int browser = checked((int)core.BrowserProcessId);
            ProcessTreeMeasurement.Snapshot owned = ProcessTreeMeasurement.Capture();
            if (owned.ProcessIds.Contains(browser) && browser != Environment.ProcessId)
            {
                using Process child = Process.GetProcessById(browser);
                if (string.Equals(child.ProcessName, "msedgewebview2", StringComparison.OrdinalIgnoreCase))
                {
                    TaskCompletionSource failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
                    {
                        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                        {
                            failed.TrySetResult();
                        }
                    }

                    core.ProcessFailed += ProcessFailed;
                    int fetchesBefore = transport.Requests;
                    // Only this unique qualification profile's browser, validated as an application descendant.
                    child.Kill();
                    bool observed = false;
                    try
                    {
                        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                        observed = true;
                    }
                    catch (TimeoutException)
                    {
                    }

                    Failure afterFailure = await reader.ReadAsync(message, false, cancellationToken);
                    results.Add(new
                    {
                        Case = "actual-reader-process-failure-fails-closed",
                        Passed = observed && afterFailure == Failure.Unavailable && transport.Requests == fetchesBefore &&
                            !window.FindControl<ContentControl>("HtmlSurface")!.IsVisible,
                        Failure = afterFailure.ToString()
                    });
                }
                else
                {
                    results.Add(new
                    {
                        Case = "actual-reader-process-failure-fails-closed",
                        Passed = false,
                        Error = "Owned browser identity mismatch"
                    });
                }
            }
            else
            {
                results.Add(new
                {
                    Case = "actual-reader-process-failure-fails-closed",
                    Passed = false,
                    Error = "Owned browser not in application process tree"
                });
            }

            async Task<bool> InspectAsync(string expression)
            {
                return await core.ExecuteScriptAsync(expression).WaitAsync(cancellationToken) == "true";
            }

            async Task FrameAsync()
            {
                TaskCompletionSource frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.InvalidateVisual();
                window.RequestAnimationFrame(_ => frame.TrySetResult());
                await frame.Task.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            await reader.DisposeAsync();
            try
            {
                await destroyed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                results.Add(new
                {
                    Case = "actual-reader-adapter-destroyed",
                    Passed = true
                });
            }
            catch (TimeoutException)
            {
                results.Add(new
                {
                    Case = "actual-reader-adapter-destroyed",
                    Passed = false
                });
            }
            window.Close();
        }
    }
}

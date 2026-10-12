using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Postfold.UI;

namespace Postfold.Desktop;

/// <summary>Opt-in synthetic measurements on the actual desktop rendering loop, never headless test timings.</summary>
public static class ClientPerformanceProbe
{
    public static async Task RunAsync(MainWindow window, MainWindowViewModel model, string output,
        long started, bool populated, CancellationToken cancellationToken)
    {
        await FrameAsync(window, cancellationToken);
        double startup = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        List<double> previews = [];
        foreach (Postfold.Core.MessageSnapshot message in model.Messages.Take(20).ToArray())
        {
            long sample = Stopwatch.GetTimestamp();
            model.Selected = message;
            await model.ReadSelectedAsync(false);
            await FrameAsync(window, cancellationToken);
            previews.Add(Stopwatch.GetElapsedTime(sample).TotalMilliseconds);
        }

        List<double> searches = [];
        for (int index = 0; index < 30; index++)
        {
            long sample = Stopwatch.GetTimestamp();
            model.Terms = index % 2 == 0 ? "résumé" : "fixture";
            await model.RefreshAsync(false);
            await FrameAsync(window, cancellationToken);
            searches.Add(Stopwatch.GetElapsedTime(sample).TotalMilliseconds);
        }

        List<double> scrolling = [];
        ScrollViewer? scroll = window.FindControl<ListBox>("MessageList")!.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null)
        {
            for (int index = 0; index < 120; index++)
            {
                long sample = Stopwatch.GetTimestamp();
                double maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                scroll.Offset = new Vector(0, maximum * (index % 60) / 59);
                await FrameAsync(window, cancellationToken);
                scrolling.Add(Stopwatch.GetElapsedTime(sample).TotalMilliseconds);
            }
        }

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        long idleStarted = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
        process.Refresh();
        double seconds = Stopwatch.GetElapsedTime(idleStarted).TotalSeconds;
        double cpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        ProcessTreeMeasurement.Snapshot memory = ProcessTreeMeasurement.Capture();
        object result = new
        {
            CapturedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            RuntimeAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            WindowWidth = window.Bounds.Width,
            WindowHeight = window.Bounds.Height,
            Scaling = window.RenderScaling,
            Theme = window.ActualThemeVariant.ToString(),
            FontSize = window.FontSize,
            SyntheticAccounts = model.Accounts.Count,
            SearchScope = model.Scope?.Name,
            ExpectedSyntheticMessages = 100000,
            ActualCachedMessages = model.CachedMessageCount,
            IncludesInitialPopulation = populated,
            StartupMilliseconds = startup,
            StartupClassification = "Process startup; OS cold/warm cache state requires operator annotation",
            PreviewSamples = previews,
            PreviewP95Milliseconds = Percentile(previews),
            SearchSamples = searches,
            SearchP95Milliseconds = Percentile(searches),
            ScrollFrameSamples = scrolling,
            ScrollFrameP95Milliseconds = Percentile(scrolling),
            IdleObservationSeconds = seconds,
            IdleTotalCpuPercent = cpu,
            PrivateBytes = memory.PrivateBytes,
            ProcessIds = memory.ProcessIds,
            MemoryComplete = memory.Complete,
            WebViews = 0,
            MemoryScope = "Application process and all descendants; no renderer constructed in this mode",
            AcceptanceComplete = false,
            Qualification = "Actual native application samples on recorded available hardware; native security/accessibility gates remain separate"
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            cancellationToken);
        window.Title = Strings.Get("MeasurementSaved");
    }

    private static double? Percentile(List<double> values)
    {
        return values.Count == 0 ? null : values.Order().ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
    }

    private static async Task FrameAsync(Window window, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        window.InvalidateVisual();
        window.RequestAnimationFrame(_ => completion.TrySetResult());
        await completion.Task;
    }
}

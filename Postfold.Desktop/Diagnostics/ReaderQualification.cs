using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Postfold.Desktop.Diagnostics;

/// <summary>Measures the real reader in its own process, without raw renderer stress-fixture allocations.</summary>
public static class ReaderQualification
{
    public static Window Create(string output)
    {
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "reader-qualification-data");
        Directory.CreateDirectory(directory);
        Window window = new Window
        {
            Title = "Postfold synthetic reader qualification",
            Width = 400,
            Height = 200
        };
        window.Opened += async (_, _) =>
        {
            List<object> results = [];
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("Windows native reader qualification is required.");
                }

                await NativeReaderQualification.RunAsync(window, directory, results, deadline.Token);
            }
            catch (Exception exception)
            {
                results.Add(new
                {
                    Case = "reader-harness",
                    Passed = false,
                    Error = exception.GetType().Name
                });
            }

            object report = new
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                Runtime = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                LogicalProcessors = Environment.ProcessorCount,
                RuntimeAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Cases = results,
                AcceptanceComplete = false
            };
            await File.WriteAllTextAsync(
                output,
                JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true
                }) + "\n"
            );
            window.Close();
        };
        return window;
    }
}

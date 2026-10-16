using Postfold.UpdateBroker.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UpdateBroker.Services;

/// <summary>Controlled boundary launcher; no production discovery, Settings or restart call site is connected.</summary>
public static class NativeUpdateLauncher
{
    /// <summary>
    /// Pins and checks the self-contained single-file broker before executing any of its bytes.
    /// The callback observes real milestones; it may authorize old-process exit after verified acknowledgement.
    /// The caller owns this operation until completion or explicitly transfers ownership by exiting after verified.
    /// </summary>
    public static async Task<int> RunAsync(
        string broker,
        NativeUpdateRequest request,
        Func<string, CancellationToken, Task> observe,
        CancellationToken cancellationToken
    )
    {
        using LockedUpdateFiles locks = new LockedUpdateFiles();
        using FileStream executable = locks.Verify(broker, request.BrokerSha256);
        ProcessStartInfo start = new ProcessStartInfo(broker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(broker)!
        };
        foreach (string name in start.Environment.Keys.Where(name =>
            name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }
        // The verified single-file image must not load a previously populated extraction cache or startup hook.
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(broker)!, ".postfold-runtime-" + Guid.NewGuid().ToString("N"));
        using Process process = Process.Start(start) ?? throw new IOException("Broker failed to start.");
        try
        {
            await observe("broker:" + process.Id + ":" + process.StartTime.ToUniversalTime().Ticks, cancellationToken).ConfigureAwait(false);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
            if (bytes.Length > 524288)
            {
                throw new InvalidDataException("Trusted request exceeds boundary limit.");
            }
            await process.StandardInput.BaseStream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? milestone;
            while ((milestone = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (milestone == "verified")
                {
                    // Send before the callback may release the old application. Pipe EOF cannot authorize apply.
                    await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                await observe(milestone, cancellationToken).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
    }
}

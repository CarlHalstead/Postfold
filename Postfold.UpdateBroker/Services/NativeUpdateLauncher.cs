using Postfold.UpdateBroker.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UpdateBroker.Services;

/// <summary>Controlled boundary launcher; executable trust is always an explicit composition input.</summary>
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
        return await RunCoreAsync(broker, request, observe, restart: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies first, awaits the real shutdown barrier, then authorizes apply and reports authorized.
    /// Only the authorized callback may request application exit. Cancellation after shutdown commitment
    /// must come from the handoff deadline, never a closed Settings window.
    /// </summary>
    public static Task<int> RunRestartAsync(
        string broker,
        NativeUpdateRequest request,
        Func<string, CancellationToken, Task> observe,
        CancellationToken cancellationToken
    )
    {
        return RunCoreAsync(broker, request, observe, restart: true, cancellationToken);
    }

    private static async Task<int> RunCoreAsync(
        string broker,
        NativeUpdateRequest request,
        Func<string, CancellationToken, Task> observe,
        bool restart,
        CancellationToken cancellationToken
    )
    {
        using LockedUpdateFiles locks = new LockedUpdateFiles();
        using FileStream executable = locks.Verify(broker, request.BrokerSha256);
        // Process.Start inherits ambient inheritable handles on the pinned runtime. The broker acquires
        // its own guards; it must not retain hidden duplicates of the launcher's installation ancestors.
        locks.PreventInheritance();
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
                    if (restart)
                    {
                        await observe(milestone, cancellationToken).ConfigureAwait(false);
                    }
                    // Send before the callback may release the old application. Pipe EOF cannot authorize apply.
                    await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (restart)
                    {
                        await observe("authorized", cancellationToken).ConfigureAwait(false);
                        continue;
                    }
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

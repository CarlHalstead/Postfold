using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateTrustCrashTests
{
    [TestCase("journal-flushed", false)]
    [TestCase("head-prepared", false)]
    [TestCase("head-promoted", false)]
    [TestCase("accepted", false)]
    [TestCase("journal-flushed", true)]
    [TestCase("head-prepared", true)]
    [TestCase("head-promoted", true)]
    [TestCase("accepted", true)]
    public async Task Open_WhenOwnerDiesAtDurableBoundaries_RecoversReleaseOrSuccessorWithoutRollback(string checkpoint, bool rotation)
    {
        // Arrange
        using UpdateFixture root = new UpdateFixture();
        using UpdateFixture successor = new UpdateFixture();
        string path = Path.Combine(EngineFixture.DirectoryPath(), "process.journal");
        byte[] initial = root.Manifest();
        byte[] next = root.Manifest(2, "1.2.0");
        byte[] activation = successor.Manifest(2, "1.2.0");
        byte[] document = rotation ? root.Transition(successor, activation) : next;
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, root.PublicKey, "stable").Value!)
        {
            Assert.That(store.Accept(initial, root.Sign(initial)).IsSuccess, Is.True);
        }
        await File.WriteAllTextAsync(path + ".public", root.PublicKey);
        await File.WriteAllBytesAsync(path + ".document", document);
        await File.WriteAllBytesAsync(path + ".signature", root.Sign(document));
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Postfold.Foundation.Tests.exe" : "Postfold.Foundation.Tests");
        ProcessStartInfo start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "--update-trust-worker", path, checkpoint, rotation ? "transition" : "manifest" })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        bool stopped = false;
        try
        {
            Assert.That(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)), Is.EqualTo(checkpoint));
            Assert.That(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)), Is.EqualTo(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // Act: terminate only this IPC-paused disposable worker, never a normal Postfold process.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            process.Dispose();
            stopped = true;
            Stopwatch reacquire = Stopwatch.StartNew();
            int attempts = 0;
            Outcome<UpdateTrustStore> opened;
            do
            {
                // Native handle teardown may briefly report sharing contention after process exit.
                // Retry only that unavailable outcome, off the caller thread; never bypass verification.
                opened = await Task.Run(() => UpdateTrustStore.Open(path, root.PublicKey, "stable"));
                attempts++;
            } while (opened.Failure == Failure.Unavailable && reacquire.Elapsed < TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("Native trust reacquisition: " + attempts + " attempts / " + reacquire.Elapsed.TotalMilliseconds + "ms");
            Assert.That(opened.IsSuccess, Is.True, opened.Failure.ToString() + ": " + path);
            using UpdateTrustStore reopened = opened.Value!;

            // Assert
            Assert.That(reopened.State.Epoch, Is.EqualTo(rotation ? 1 : 0));
            Assert.That(reopened.State.Sequence, Is.EqualTo(rotation ? 1 : 2));
            if (rotation)
            {
                Assert.That(reopened.State.PublicKey, Is.EqualTo(successor.PublicKey));
                Assert.That(reopened.Accept(initial, root.Sign(initial)).Failure, Is.EqualTo(Failure.Validation));
                Assert.That(reopened.Accept(activation, successor.Sign(activation)).IsSuccess, Is.True);
            }
            else
            {
                Outcome<UpdateTrustState> replay = reopened.Accept(initial, root.Sign(initial));
                Assert.That(replay.Failure, Is.EqualTo(Failure.PolicyBlocked));
            }
        }
        finally
        {
            if (!stopped && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}

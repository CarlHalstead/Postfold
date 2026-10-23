using NUnit.Framework;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class BlobStoreTests
{
    [Test]
    public async Task VerifyPath_WhenLeafIsMissing_StillRejectsRedirectedAncestor()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string target = Path.Combine(directory, "target");
        string redirect = Path.Combine(directory, "redirect");
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsWindows())
        {
            // Directory junctions exercise Windows reparse checks without symbolic-link privileges.
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(redirect);
            start.ArgumentList.Add(target);
            using Process command = Process.Start(start)!;
            TestContext.Out.WriteLine("Junction command PID " + command.Id + ", started " + command.StartTime.ToString("o"));
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await command.WaitForExitAsync(deadline.Token);
                Assert.That(command.ExitCode, Is.Zero);
            }
            finally
            {
                if (!command.HasExited)
                {
                    command.Kill(entireProcessTree: true);
                    await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
        else
        {
            Directory.CreateSymbolicLink(redirect, target);
        }

        // Act / Assert
        Assert.That(() => BlobStore.VerifyPath(Path.Combine(redirect, "missing", "profile.db-wal")), Throws.TypeOf<IOException>());
    }
}

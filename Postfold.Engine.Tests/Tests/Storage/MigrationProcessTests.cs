using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Engine.Tests.Fixtures.Storage;
using Postfold.Storage.Models;
using Postfold.Storage.Services;
using Postfold.Storage.Stores;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class MigrationProcessTests
{
    [TestCase("lease-acquired")]
    [TestCase("receipt-prepared")]
    [TestCase("before-initial-ddl")]
    [TestCase("before-format-guard")]
    [TestCase("history-in-transaction")]
    [TestCase("migrations-committed")]
    [TestCase("receipt-complete")]
    public async Task AdmitAsync_WhenCreatingProcessDies_PreservesCommittedPrefixAndReleasesLease(string stage)
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        using Process child = Start(database, stage, "1");
        await BarrierAsync(child, stage);

        // Act
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        Outcome<SqliteProfile> reopened = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(reopened.IsSuccess, Is.True);
        if (reopened.Value is not null)
        {
            await reopened.Value.DisposeAsync();
        }

        using FileStream lease = new FileStream(database + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(database))
        {
            using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
            Assert.That(ProfileMigration.Scalar(connection, "PRAGMA quick_check;"), Is.EqualTo("ok"));
            Assert.That(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), Is.AnyOf(0L, 2L));
        }
    }

    [TestCase("backup-complete")]
    [TestCase("receipt-prepared")]
    [TestCase("before-rebuild")]
    [TestCase("migrations-committed")]
    public async Task AdmitAsync_WhenUpgradeProcessDies_PreservesRowsAndValidatedBackup(string stage)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await MigrationTests.PopulateAsync(directory);
        string original = ProfileEvidence.Rows(database);
        using Process child = Start(database, stage, "3");
        await BarrierAsync(child, stage);

        // Act
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        ProfileMigrationPlan plan = EvolutionContext.CreatePlan();
        using (FileStream lease = await ProfileMigration.AdmitAsync(database, plan, CancellationToken.None))
        {
        }

        // Assert
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        foreach (string backup in Directory.GetDirectories(directory, "*.before-migration-*"))
        {
            ProfileBackup.Validate(backup);
        }

        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA quick_check;"), Is.EqualTo("ok"));
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA foreign_key_check;"), Is.Null);
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), Is.EqualTo(4L));
    }

    [Test]
    public async Task OpenAsync_WhenAnotherProcessOwnsProfile_RefusesUntilThatProcessExits()
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        using Process child = Start(database, "admitted", "1");
        await BarrierAsync(child, "admitted");

        // Act
        Outcome<SqliteProfile> competing = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        Outcome<SqliteProfile> reopened = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(competing.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(reopened.IsSuccess, Is.True);
        await reopened.Value!.DisposeAsync();
    }

    private static Process Start(string database, string stage, string count)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Postfold.Engine.Tests.exe" : "Postfold.Engine.Tests");
        ProcessStartInfo start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(stage);
        start.ArgumentList.Add(count);
        return Process.Start(start) ?? throw new InvalidOperationException("Synthetic process failed to start.");
    }

    private static async Task BarrierAsync(Process child, string stage)
    {
        try
        {
            string? result;
            do
            {
                result = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            } while (result is not null && result != stage);

            if (result != stage)
            {
                throw new InvalidOperationException(await child.StandardError.ReadToEndAsync());
            }
        }
        catch
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            throw;
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Engine.Tests.Fixtures.Storage;
using Postfold.Storage.Models;
using Postfold.Storage.Services;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class MigrationFailureTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmitAsync_WhenSqliteUpgradeFails_RollsBackRowsHistoryAndFormat(bool diskFull)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await MigrationTests.PopulateAsync(directory);
        string original = ProfileEvidence.Rows(database);
        ProfileMigrationPlan plan = EvolutionContext.CreatePlan();
        plan = plan with
        {
            ApplyAsync = async (connection, target, cancellationToken) =>
            {
                if (diskFull)
                {
                    long pages = Convert.ToInt64(ProfileMigration.Scalar(connection, "PRAGMA page_count;"), System.Globalization.CultureInfo.InvariantCulture);
                    ProfileMigration.Scalar(connection, "PRAGMA max_page_count=" + pages.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";");
                }

                using EvolutionContext context = new EvolutionContext(new DbContextOptionsBuilder<EvolutionContext>()
                    .UseSqlite(connection)
                    .AddInterceptors(new MigrationFailureInterceptor(diskFull))
                    .Options);
                await context.GetService<IMigrator>().MigrateAsync(target, cancellationToken).ConfigureAwait(false);
            }
        };

        // Act
        Exception? exception = await Assert.CatchAsync(async () =>
            await ProfileMigration.AdmitAsync(database, plan, CancellationToken.None));

        // Assert
        if (diskFull)
        {
            Assert.That(exception, Is.TypeOf<SqliteException>());
            Assert.That(((SqliteException)exception!).SqliteErrorCode, Is.EqualTo(13));
        }
        else
        {
            Assert.That(exception, Is.TypeOf<ProfileAdmissionException>());
            Assert.That(((ProfileAdmissionException)exception!).Failure, Is.EqualTo(Failure.NeedsReview));
        }
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        using SqliteConnection read = ProfileMigration.Connect(database, readOnly: true);
        Assert.That(ProfileMigration.Scalar(read, "PRAGMA user_version;"), Is.EqualTo(2L));
        Assert.That(ProfileMigration.Scalar(read, "SELECT count(*) FROM __EFMigrationsHistory;"), Is.EqualTo(1L));
        foreach (string backup in Directory.GetDirectories(directory, "*.before-migration-*"))
        {
            ProfileBackup.Validate(backup);
        }

        using FileStream recovered = await ProfileMigration.AdmitAsync(database, EvolutionContext.CreatePlan(), CancellationToken.None);
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
    }

    [Test]
    public async Task AdmitAsync_WhenCommittedRowsRemainInWal_BackupIncludesThemWithoutCheckpointingSource()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await MigrationTests.PopulateAsync(directory);
        using SqliteConnection writer = ProfileMigration.Connect(database, readOnly: false);
        ProfileEvidence.Execute(writer, "PRAGMA wal_autocheckpoint=0; UPDATE Drafts SET Revision=Revision+7; UPDATE Preferences SET Value='WAL evidence';");
        string original = ProfileEvidence.Rows(database);
        Assert.That(new FileInfo(database + "-wal").Length, Is.GreaterThan(0));

        // Act
        using FileStream lease = await ProfileMigration.AdmitAsync(database, EvolutionContext.CreatePlan(), CancellationToken.None);
        string backup = Directory.GetDirectories(directory, "*.before-migration-*").Single();
        ProfileBackup.Validate(backup);

        // Assert
        Assert.That(ProfileEvidence.Rows(Path.Combine(backup, "profile.db")), Is.EqualTo(original));
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
    }

    [Test]
    public async Task AdmitAsync_WhenProtectedObjectCannotBeRead_AbortsBeforeDdlAndReleasesLease()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await MigrationTests.PopulateAsync(directory);
        string original = ProfileEvidence.Rows(database);
        string owner = Directory.GetFiles(Path.Combine(directory, "objects"), "*.owner").First();

        // Act
        using (FileStream denyRead = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(async () =>
                await ProfileMigration.AdmitAsync(database, EvolutionContext.CreatePlan(), CancellationToken.None));
        }

        // Assert
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        using SqliteConnection read = ProfileMigration.Connect(database, readOnly: true);
        Assert.That(ProfileMigration.Scalar(read, "PRAGMA user_version;"), Is.EqualTo(2L));
        using FileStream lease = await ProfileMigration.AdmitAsync(database, EvolutionContext.CreatePlan(), CancellationToken.None);
    }

    [Test]
    public async Task OpenAsync_WhenLeasePathIsUnavailable_ReturnsSafeFailureWithoutTouchingDatabase()
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        Directory.CreateDirectory(database + ".lease");

        // Act
        Outcome<SqliteProfile> outcome = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(outcome.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(File.Exists(database), Is.False);
    }
}

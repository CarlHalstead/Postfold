using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Engine.Tests.Fixtures.Storage;
using Postfold.Storage.Contexts;
using Postfold.Storage.Models;
using Postfold.Storage.Services;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class MigrationTests
{
    [TestCase("\n")]
    [TestCase("\r\n")]
    public void SchemaHash_WhenMigrationSqlUsesPlatformLineEndings_MatchesReviewedManifest(
        string newline
    )
    {
        // Arrange
        using SqliteConnection connection = ProfileMigration.Connect(":memory:", readOnly: false);
        using ProfileDbContext context = new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite(connection).Options);
        string script = context.GetService<IMigrator>().GenerateScript().ReplaceLineEndings(newline);
        ProfileEvidence.Execute(connection, script);

        // Act
        string schema = ProfileMigration.SchemaHash(connection);

        // Assert
        Assert.That(schema, Is.EqualTo(ProfileMigrationManifest.Versions[^1].SchemaHash));
        ProfileEvidence.Execute(connection, "DROP INDEX MessageDate;");
        Assert.That(ProfileMigration.SchemaHash(connection), Is.Not.EqualTo(schema));
    }

    [Test]
    public async Task OpenAsync_WhenFreshThenReopened_PreservesReviewedSchemaAndHistory()
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");

        // Act
        Outcome<SqliteProfile> opened = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        await opened.Value!.DisposeAsync();
        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
        string schema = ProfileMigration.SchemaHash(connection);
        using ProfileDbContext context = new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite(connection).Options);
        Outcome<SqliteProfile> reopened = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        await reopened.Value!.DisposeAsync();

        // Assert
        Assert.That(opened.IsSuccess, Is.True);
        Assert.That(reopened.IsSuccess, Is.True);
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), Is.EqualTo(3L));
        Assert.That(context.Database.GetAppliedMigrations(), Is.EqualTo(ProfileMigrationManifest.Versions.Select(item => item.Id)));
        Assert.That(context.Database.HasPendingModelChanges(), Is.False);
        Assert.That(EvolutionContext.CreatePlan().Versions[0], Is.EqualTo(ProfileMigrationManifest.Versions[0]));
        Assert.That(ProfileMigration.SchemaHash(connection), Is.EqualTo(schema));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='SchemaMigrations';"), Is.EqualTo(0L));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='index' AND sql IS NOT NULL;"), Is.EqualTo(6L));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT sql FROM sqlite_master WHERE name='MessageDate';"), Does.Contain("DESC"));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT sql FROM sqlite_master WHERE name='IdentityPrimary';"), Does.Contain("WHERE IsPrimary=1"));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT sql FROM sqlite_master WHERE name='SearchText';"), Does.Contain("unicode61 remove_diacritics 2"));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE sql LIKE '%AUTOINCREMENT%' OR sql LIKE '%CASCADE%';"), Is.EqualTo(0L));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task OpenAsync_WhenOccupiedWithoutOwnership_RefusesByteForByte(int content)
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        if (content == 0)
        {
            await File.WriteAllBytesAsync(database, []);
        }
        else
        {
            using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: false);
            ProfileEvidence.Execute(connection, content == 1 ? "PRAGMA user_version=0;" : "CREATE TABLE Sentinel(Value TEXT); INSERT INTO Sentinel VALUES('private evidence');");
        }

        byte[] original = await File.ReadAllBytesAsync(database);

        // Act
        Outcome<SqliteProfile> outcome = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(outcome.IsSuccess, Is.False);
        Assert.That(await File.ReadAllBytesAsync(database), Is.EqualTo(original));
        Assert.That(File.Exists(database + ".migration.json"), Is.False);
        Assert.That(Directory.Exists(Path.Combine(Path.GetDirectoryName(database)!, "objects")), Is.False);
    }

    [TestCase("PRAGMA user_version=999;", Failure.NewerSchema)]
    [TestCase("INSERT INTO __EFMigrationsHistory VALUES('20990101000000_Future','10.0.13');", Failure.NewerSchema)]
    [TestCase("PRAGMA user_version=0;", Failure.NeedsReview)]
    [TestCase("DROP INDEX MessageDate;", Failure.NeedsReview)]
    [TestCase("DELETE FROM __EFMigrationsHistory;", Failure.NeedsReview)]
    public async Task OpenAsync_WhenEfMetadataIsUnsupported_RefusesBeforeWritableAdmission(string mutation, Failure failure)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.DisposeAsync();
        string database = Path.Combine(directory, "profile.db");
        using (SqliteConnection connection = ProfileMigration.Connect(database, readOnly: false))
        {
            ProfileEvidence.Execute(connection, mutation);
        }

        byte[] original = await File.ReadAllBytesAsync(database);

        // Act
        Outcome<SqliteProfile> outcome = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(outcome.Failure, Is.EqualTo(failure));
        Assert.That(await File.ReadAllBytesAsync(database), Is.EqualTo(original));
    }

    [Test]
    public async Task OpenAsync_WhenAnotherOwnerHoldsLease_RefusesThenAdmitsAfterDisposal()
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        Outcome<SqliteProfile> first = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Act
        Outcome<SqliteProfile> second = await SqliteProfile.OpenAsync(Path.Combine(Path.GetDirectoryName(database)!, ".", "profile.db"), TimeProvider.System, CancellationToken.None);
        await first.Value!.DisposeAsync();
        Outcome<SqliteProfile> third = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        await third.Value!.DisposeAsync();

        // Assert
        Assert.That(second.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(third.IsSuccess, Is.True);
    }

    [Test]
    public async Task OpenAsync_WhenCancelled_ReleasesOwnershipWithoutCreatingDatabase()
    {
        // Arrange
        string database = Path.Combine(EngineFixture.DirectoryPath(), "profile.db");
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await SqliteProfile.OpenAsync(database, TimeProvider.System, cancellation.Token));

        // Assert
        Assert.That(File.Exists(database), Is.False);
        Outcome<SqliteProfile> retry = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        Assert.That(retry.IsSuccess, Is.True);
        await retry.Value!.DisposeAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmitAsync_WhenPopulatedEfOriginUpgrades_PreservesEveryRowAndProtectedBlob(
        bool intermediate
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await PopulateAsync(directory);
        string original = ProfileEvidence.Rows(database);
        ProfileMigrationPlan plan = EvolutionContext.CreatePlan();

        // Act
        if (intermediate)
        {
            using FileStream first = await ProfileMigration.AdmitAsync(database, plan with
            {
                Versions = plan.Versions.Take(3).ToArray()
            }, CancellationToken.None);
        }

        using FileStream lease = await ProfileMigration.AdmitAsync(database, plan, CancellationToken.None);
        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
        string backup = Directory.GetDirectories(directory, "*.before-migration-*").First();
        ProfileBackup.Validate(backup);

        // Assert
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), Is.EqualTo(5L));
        Assert.That(ProfileMigration.SchemaHash(connection), Is.EqualTo(plan.Versions[^1].SchemaHash));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT count(*) FROM __EFMigrationsHistory;"), Is.EqualTo(4L));
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA foreign_key_check;"), Is.Null);
        Assert.That(Directory.GetFiles(Path.Combine(backup, "objects"), "*.owner"), Has.Length.GreaterThanOrEqualTo(3));
        Assert.That(Directory.GetFiles(Path.Combine(backup, "objects"), "*.tmp"), Has.Length.EqualTo(1));
        Assert.That(ProfileMigration.Scalar(connection, "SELECT State FROM OutboxSubmissions ORDER BY Created LIMIT 1;"), Is.EqualTo((long)SubmissionState.Submitting));
    }

    [Test]
    public async Task AdmitAsync_WhenProtectedObjectIsMissing_AbortsBeforeHistoryOrSchemaChanges()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await PopulateAsync(directory);
        string blob = Directory.GetFiles(Path.Combine(directory, "objects"), "*.blob").First();
        File.Move(blob, blob + ".synthetic-missing");
        string original = ProfileEvidence.Rows(database);

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ProfileMigration.AdmitAsync(database, EvolutionContext.CreatePlan(), CancellationToken.None));

        // Assert
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), Is.EqualTo(3L));
        Assert.That(Directory.GetFiles(directory, "complete.json", SearchOption.AllDirectories), Is.Empty);
    }

    [Test]
    public async Task OpenAsync_WhenAbandonedEfLockIsPresent_ReturnsBoundedReviewWithoutStealing()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.DisposeAsync();
        string database = Path.Combine(directory, "profile.db");
        using (SqliteConnection connection = ProfileMigration.Connect(database, readOnly: false))
        {
            ProfileEvidence.Execute(connection, "INSERT INTO __EFMigrationsLock VALUES(1,'2000-01-01 00:00:00');");
        }

        // Act
        Outcome<SqliteProfile> result = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        using SqliteConnection read = ProfileMigration.Connect(database, readOnly: true);
        Assert.That(ProfileMigration.Scalar(read, "SELECT count(*) FROM __EFMigrationsLock;"), Is.EqualTo(1L));
    }

    internal static async Task PopulateAsync(
        string directory
    )
    {
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.AddAccountAsync(new AccountId(3), "Inactive synthetic", CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        BlobReference body = await EngineFixture.BlobAsync(profile, "Protected café content");
        await profile.StoreBodyAsync(message.Account, message.Id, body, "Protected café content", CancellationToken.None);
        DraftSnapshot draft = await profile.SaveAsync(Guid.NewGuid(), EngineFixture.Account, body, CancellationToken.None);
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "Frozen immutable MIME"));
        await profile.QueueAsync(submission, CancellationToken.None);
        await profile.ClaimAsync(submission.Id, submission.Account, CancellationToken.None);
        foreach (SubmissionState state in new[] { SubmissionState.Queued, SubmissionState.Uncertain, SubmissionState.PartiallyAccepted, SubmissionState.SentCopyPending })
        {
            FrozenSubmission additional = EngineFixture.Submission(body);
            await profile.QueueAsync(additional, CancellationToken.None);
            using SqliteConnection mutate = ProfileMigration.Connect(Path.Combine(directory, "profile.db"), readOnly: false);
            using SqliteCommand command = mutate.CreateCommand();
            command.CommandText = "UPDATE OutboxSubmissions SET State=$state,Evidence=$evidence WHERE SubmissionId=$id;";
            command.Parameters.AddWithValue("$state", (int)state);
            SubmissionOutcome evidence = new SubmissionOutcome(
                state,
                state == SubmissionState.Uncertain ? SubmissionPhase.AwaitingAcknowledgement : SubmissionPhase.Acknowledged,
                [new RecipientEvidence("first@synthetic.invalid", RecipientState.Accepted, 250),
                    new RecipientEvidence("second@synthetic.invalid", state == SubmissionState.PartiallyAccepted ? RecipientState.Rejected : RecipientState.Uncertain, null)],
                Failure.NeedsReview,
                "synthetic server receipt"
            );
            command.Parameters.AddWithValue("$evidence", state == SubmissionState.Queued ? DBNull.Value : JsonSerializer.Serialize(evidence));
            command.Parameters.AddWithValue("$id", additional.Id.ToString("N"));
            command.ExecuteNonQuery();
        }

        await profile.CommitAsync(
            EngineFixture.Mailbox(account: new AccountId(2)),
            EngineFixture.Page([EngineFixture.Message(1, account: new AccountId(2))], account: new AccountId(2)),
            CancellationToken.None
        );
        using MemoryStream secondBytes = new MemoryStream(Encoding.UTF8.GetBytes("Second account protected draft"));
        BlobReference secondBlob = await profile.Blobs.WriteAsync(new AccountId(2), secondBytes, 100, true, CancellationToken.None);
        await profile.SaveAsync(Guid.NewGuid(), new AccountId(2), secondBlob, CancellationToken.None);
        await profile.QueueAsync(EngineFixture.Submission(secondBlob) with
        {
            Account = new AccountId(2)
        }, CancellationToken.None);
        await EngineFixture.BlobAsync(profile, "Protected orphan");
        using MemoryStream oversized = new MemoryStream(Encoding.UTF8.GetBytes("Interrupted protected blob"));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await profile.Blobs.WriteAsync(EngineFixture.Account, oversized, 1, true, CancellationToken.None));
        using SqliteConnection connection = ProfileMigration.Connect(Path.Combine(directory, "profile.db"), readOnly: false);
        ProfileEvidence.Execute(connection, """
            UPDATE Accounts SET Active=0 WHERE AccountId=3;
            INSERT INTO Preferences VALUES('synthetic','density','compact',1);
            INSERT INTO Identities VALUES(10,1,'one@synthetic.invalid','One','signature one',1);
            INSERT INTO Identities VALUES(20,2,'two@synthetic.invalid','Two','signature two',1);
            UPDATE Drafts SET IdentityId=CASE AccountId WHEN 1 THEN 10 ELSE 20 END;
            INSERT INTO Attachments SELECT 1,AccountId,MessageId,'2','synthetic.bin','application/octet-stream',12,BlobId FROM Bodies LIMIT 1;
            INSERT INTO DraftVersions SELECT DraftId,AccountId,'remote-conflict',BlobId,'Remote',123 FROM Drafts;
            UPDATE Drafts SET Conflict=1,Held=1,RemoteVersion='opaque-revision';
            INSERT INTO PendingOperations SELECT 'abcdefabcdefabcdefabcdefabcdefab',AccountId,MessageId,1,'{}',6,17,2,123,1 FROM Messages LIMIT 1;
            UPDATE SubmissionRecipients SET State=CASE Ordinal WHEN 0 THEN 1 ELSE 3 END,ReplyCode=250;
            """);
    }
}

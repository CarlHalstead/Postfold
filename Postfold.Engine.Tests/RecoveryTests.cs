using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Storage;

namespace Postfold.Engine.Tests;

public sealed class RecoveryTests
{
    [Test]
    public async Task RecoverAsync_WhenSqlitePageQuotaIsExhausted_RollsBackAndPreservesUnsentWork()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        DraftSnapshot draft = await profile.SaveAsync(Guid.NewGuid(), EngineFixture.Account,
            await EngineFixture.BlobAsync(profile, "unsent draft"), CancellationToken.None);
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "frozen submission"));
        await profile.QueueAsync(frozen, CancellationToken.None);
        await profile.ClaimAsync(EngineFixture.Account, CancellationToken.None);
        await profile.DisposeAsync();
        int errorCode;

        // Act
        using (SqliteConnection connection = new SqliteConnection("Data Source=" + Path.Combine(path, "profile.db") +
            ";Pooling=False"))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA page_count;";
            long pages = (long)(await command.ExecuteScalarAsync())!;
            command.CommandText = "PRAGMA max_page_count=" + pages.ToString(CultureInfo.InvariantCulture) + ";";
            await command.ExecuteScalarAsync();
            using SqliteTransaction transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Drafts SET Revision=Revision+1 WHERE DraftId=$id;";
            command.Parameters.AddWithValue("$id", draft.Id.ToString("N"));
            await command.ExecuteNonQueryAsync();
            command.Parameters.Clear();
            command.CommandText = "UPDATE Accounts SET Configuration=$content WHERE AccountId=1;";
            command.Parameters.AddWithValue("$content", new string('x', 1048576));
            SqliteException failure = await Assert.ThrowsAsync<SqliteException>(async () =>
                await command.ExecuteNonQueryAsync()) ?? throw new InvalidOperationException("Missing disk-full evidence.");
            errorCode = failure.SqliteErrorCode;
        }

        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        await reopened.RecoverAsync(CancellationToken.None);
        DraftSnapshot? restored = await reopened.ReadAsync(draft.Id, draft.Account, CancellationToken.None);
        OutboxEntry? submission = await ((IOutboxStore)reopened).ReadAsync(frozen.Id, frozen.Account, CancellationToken.None);
        await using Stream retained = await reopened.Blobs.OpenAsync(draft.Content, CancellationToken.None);

        // Assert
        Assert.That(errorCode, Is.EqualTo(13));
        Assert.That(restored, Is.EqualTo(draft));
        Assert.That(submission!.State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(retained.Length, Is.EqualTo(draft.Content.Length));
    }

    [Test]
    public async Task OpenAsync_WhenMigrationFails_PreservesOriginalAndConsistentBackup()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        string database = Path.Combine(path, "profile.db");
        using (SqliteConnection connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Accounts(Sentinel TEXT); INSERT INTO Accounts VALUES('unsent evidence');";
            command.ExecuteNonQuery();
        }

        // Act
        Outcome<SqliteProfile> result = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);
        string backup = Directory.GetFiles(path, "*.before-migration-*").Single(file => !file.EndsWith(".json", StringComparison.Ordinal));
        using SqliteConnection saved = new SqliteConnection("Data Source=" + backup + ";Pooling=False");
        saved.Open();
        using SqliteCommand read = saved.CreateCommand();
        read.CommandText = "SELECT Sentinel FROM Accounts;";
        string? sentinel = (string?)read.ExecuteScalar();

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(sentinel, Is.EqualTo("unsent evidence"));
        Assert.That(File.Exists(database), Is.True);
        Assert.That(File.Exists(backup + ".protected.json"), Is.True);
    }

    [Test]
    public async Task OpenAsync_WhenDatabaseIsCorrupt_PreservesOriginalAndRefusesReset()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        string database = Path.Combine(path, "profile.db");
        byte[] corrupt = Encoding.ASCII.GetBytes("synthetic corrupt profile preserving unsent evidence");
        await File.WriteAllBytesAsync(database, corrupt);

        // Act
        Outcome<SqliteProfile> result = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(await File.ReadAllBytesAsync(database), Is.EqualTo(corrupt));
        Assert.That(Directory.GetDirectories(path, "*.recovery-*"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task WriteAsync_WhenInputExceedsBudget_LeavesNoPromotedReference()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        BlobStore blobs = new BlobStore(path);
        using MemoryStream source = new MemoryStream(new byte[1000]);
        await

                // Act
                Assert.ThrowsAsync<InvalidDataException>(async () => await blobs.WriteAsync(EngineFixture.Account,
            source, 10, protect: true, CancellationToken.None));

        // Assert
        Assert.That(Directory.GetFiles(path, "*.blob"), Is.Empty);
        Assert.That(Directory.GetFiles(path, "*.tmp"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task EvictAsync_WhenProtectedBodyExists_DoesNotEvictUnsentOwnership()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        BlobReference protectedContent = await EngineFixture.BlobAsync(profile, "protected");
        await profile.StoreBodyAsync(message.Account, message.Id, protectedContent, "protected", CancellationToken.None);

        // Act
        await profile.EvictAsync(message.Account, 0, CancellationToken.None);
        MessageSnapshot after = await EngineFixture.FirstAsync(profile);
        await using Stream content = await profile.Blobs.OpenAsync(protectedContent, CancellationToken.None);

        // Assert
        Assert.That(after.Availability, Is.EqualTo(ContentAvailability.Available));
        Assert.That(content.Length, Is.EqualTo(protectedContent.Length));
    }
}

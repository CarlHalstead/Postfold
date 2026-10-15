using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class RecoveryTests
{
    [Test]
    public async Task RecoverAsync_WhenOperationsAreSparse_ReconcilesOnlyTheirAccountScopedProjections()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        AccountId other = new AccountId(2);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]), CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(account: other), EngineFixture.Page([EngineFixture.Message(1, account: other)], account: other), CancellationToken.None);
        MessageSnapshot active = await EngineFixture.FirstAsync(profile);
        MessageSnapshot terminal = await EngineFixture.FirstAsync(profile, other);
        MessageSnapshot untouched = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", null, false, 10), CancellationToken.None))
            .Value!.Messages.Single(message => message.Id != active.Id);
        foreach (MessageSnapshot message in new[] { active, terminal })
        {
            await profile.QueueAsync(new MutationIntent(
                new OperationId(Guid.NewGuid()),
                message.Account,
                message.Id,
                message.Provider,
                MutationKind.SetRead,
                true,
                null
            ), CancellationToken.None);
        }
        using (SqliteConnection connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "profile.db") + ";Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE PendingOperations SET State=CASE WHEN AccountId=$account THEN $inflight ELSE $completed END; " +
                "UPDATE Messages SET Pending=$review WHERE AccountId=$a AND MessageId=$m;";
            command.Parameters.AddWithValue("$account", EngineFixture.Account.Value);
            command.Parameters.AddWithValue("$inflight", (int)PendingState.InFlight);
            command.Parameters.AddWithValue("$completed", (int)PendingState.Completed);
            command.Parameters.AddWithValue("$review", (int)PendingState.NeedsReview);
            command.Parameters.AddWithValue("$a", untouched.Account.Value);
            command.Parameters.AddWithValue("$m", untouched.Id.Value);
            command.ExecuteNonQuery();
        }

        // Act
        await profile.RecoverAsync(CancellationToken.None);
        await profile.RecoverAsync(CancellationToken.None);
        SearchPage recovered = (await profile.SearchAsync(new SearchRequest(null, true, "", null, false, 10), CancellationToken.None)).Value!;

        // Assert
        Assert.That(recovered.Messages.Single(message => message.Id == active.Id).Pending, Is.EqualTo(PendingState.Queued));
        Assert.That(recovered.Messages.Single(message => message.Id == terminal.Id).Pending, Is.EqualTo(PendingState.None));
        Assert.That(recovered.Messages.Single(message => message.Id == untouched.Id).Pending, Is.EqualTo(PendingState.NeedsReview));
    }

    [Test]
    public async Task RecoverAsync_WhenSqlitePageQuotaIsExhausted_RollsBackAndPreservesUnsentWork()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        DraftSnapshot draft = await profile.SaveAsync(
            Guid.NewGuid(),
            EngineFixture.Account,
            await EngineFixture.BlobAsync(profile, "unsent draft"),
            CancellationToken.None
        );
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
    public async Task OpenAsync_WhenDatabaseIsOccupied_RefusesWithoutChangingEvidence()
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

        byte[] before = await File.ReadAllBytesAsync(database);

        // Act
        Outcome<SqliteProfile> result = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(await File.ReadAllBytesAsync(database), Is.EqualTo(before));
        Assert.That(Directory.GetDirectories(path, "*.before-migration-*"), Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(path, "objects")), Is.False);
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
                Assert.ThrowsAsync<InvalidDataException>(async () => await blobs.WriteAsync(
                    EngineFixture.Account,
                    source,
                    10,
                    protect: true,
                    CancellationToken.None
                ));

        // Assert
        Assert.That(Directory.GetFiles(path, "*.blob"), Is.Empty);
        Assert.That(Directory.GetFiles(path, "*.tmp"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task EvictAsync_WhenProtectedBodyExists_DoesNotEvictUnsentOwnership()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
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

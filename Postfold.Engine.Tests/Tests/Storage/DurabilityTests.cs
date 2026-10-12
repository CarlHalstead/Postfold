using Microsoft.Data.Sqlite;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class DurabilityTests
{
    [Test]
    public async Task ClaimAsync_WhenEarlierMutationIsHeld_DoesNotExecuteLaterIntentOutOfOrder()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        MutationIntent first = new MutationIntent(
            new OperationId(Guid.NewGuid()),
            message.Account,
            message.Id,
            message.Provider,
            MutationKind.SetRead,
            true,
            null
        );
        MutationIntent second = first with
        {
            Id = new OperationId(Guid.NewGuid()),
            Value = false
        };
        await profile.QueueAsync(first, CancellationToken.None);
        await profile.QueueAsync(second, CancellationToken.None);
        OperationEntry claim = (await profile.ClaimAsync(
            message.Account,
            DateTimeOffset.MaxValue,
            CancellationToken.None
        ))!;
        await profile.FinishAsync(
            claim,
            PendingState.NeedsReview,
            Failure.NeedsReview,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None
        );

        // Act
        OperationEntry? blocked = await profile.ClaimAsync(
            message.Account,
            DateTimeOffset.MaxValue,
            CancellationToken.None
        );
        await profile.ResolveAsync(first.Id, message.Account, false, CancellationToken.None);
        OperationEntry? next = await profile.ClaimAsync(
            message.Account,
            DateTimeOffset.MaxValue,
            CancellationToken.None
        );
        MessageSnapshot projection = await EngineFixture.FirstAsync(profile);

        // Assert
        Assert.That(blocked, Is.Null);
        Assert.That(next!.Intent.Id, Is.EqualTo(second.Id));
        Assert.That(projection.Read, Is.False);
    }

    [Test]
    public async Task ClaimAsync_WhenFrozenPayloadIdentityIsCorrupted_RefusesNetworkClaim()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "frozen"));
        await profile.QueueAsync(frozen, CancellationToken.None);
        using (SqliteConnection connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "profile.db")))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE OutboxSubmissions SET Frozen=json_set(Frozen,'$.Account.Value',2);";
            await command.ExecuteNonQueryAsync();
        }

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await profile.ClaimAsync(EngineFixture.Account, CancellationToken.None));
    }

    [Test]
    public async Task WriteAsync_WhenStorageWriteFails_PreservesPreviousDraftAndInterruptedObject()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        Guid id = Guid.NewGuid();
        DraftSnapshot previous = await profile.SaveAsync(
            id,
            EngineFixture.Account,
            await EngineFixture.BlobAsync(profile, "unsent draft"),
            CancellationToken.None
        );
        Stream failing = Substitute.For<Stream>();
        failing.ReadAsync(Arg.Any<Memory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<int>(new IOException("Synthetic disk failure")));

        // Act
        await Assert.ThrowsAsync<IOException>(async () => await profile.Blobs.WriteAsync(
            EngineFixture.Account,
            failing,
            1048576,
            true,
            CancellationToken.None
        ));
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        DraftSnapshot? saved = await reopened.ReadAsync(id, EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(saved, Is.EqualTo(previous));
        Assert.That(Directory.GetFiles(Path.Combine(directory, "objects"), "*.tmp"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task CleanupDisposableOrphansAsync_WhenOwnershipIsProven_RetainsReferencedAndProtectedObjects()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        BlobReference referenced = await EngineFixture.BlobAsync(profile, "cached", false);
        BlobReference orphan = await EngineFixture.BlobAsync(profile, "orphan", false);
        BlobReference protectedOrphan = await EngineFixture.BlobAsync(profile, "unsent", true);
        await profile.StoreBodyAsync(message.Account, message.Id, referenced, "cached", CancellationToken.None);
        string objects = Path.Combine(directory, "objects");
        foreach (string file in Directory.GetFiles(objects, "*.owner"))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        }

        // Act
        int cleaned = await profile.CleanupDisposableOrphansAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            CancellationToken.None
        );

        // Assert
        Assert.That(cleaned, Is.EqualTo(1));
        Assert.That(File.Exists(Path.Combine(objects, referenced.Id.ToString("N") + ".blob")), Is.True);
        Assert.That(File.Exists(Path.Combine(objects, protectedOrphan.Id.ToString("N") + ".blob")), Is.True);
        Assert.That(File.Exists(Path.Combine(objects, orphan.Id.ToString("N") + ".blob")), Is.False);
    }
}

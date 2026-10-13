using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class StorageTests
{
    [Test]
    public async Task CommitAsync_WhenAccountIdentityAndHeaderOverlap_KeepsIndependentRows()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        AccountId second = new AccountId(2);

        // Act
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]),
            CancellationToken.None
        );
        await profile.CommitAsync(
            EngineFixture.Mailbox(account: second),
            EngineFixture.Page([EngineFixture.Message(1, account: second)], account: second),
            CancellationToken.None
        );
        Outcome<SearchPage> own = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "cafe",
            null,
            false,
            20
        ), CancellationToken.None);
        Outcome<SearchPage> unified = await profile.SearchAsync(new SearchRequest(
            null,
            true,
            "résumé",
            null,
            false,
            20
        ), CancellationToken.None);

        // Assert
        Assert.That(own.Value!.Messages, Has.Length.EqualTo(2));
        Assert.That(own.Value.Messages.All(item => item.Account == EngineFixture.Account), Is.True);
        Assert.That(unified.Value!.Messages, Has.Length.EqualTo(3));
    }

    [Test]
    public async Task CommitAsync_WhenCancelled_DoesNotAdvanceCheckpoint()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await

        // Act
        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            cancelled.Token
        ));
        Checkpoint? checkpoint = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);

        // Assert
        Assert.That(checkpoint, Is.Null);
    }

    [Test]
    public async Task CommitAsync_WhenPageFailsConstraint_RollsBackMessagesAndCheckpoint()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        MessageSnapshot invalid = EngineFixture.Message(2) with
        {
            Size = -1
        };
        await

                // Act
                Assert.ThrowsAsync<SqliteException>(async () => await profile.CommitAsync(
                    EngineFixture.Mailbox(),
                    EngineFixture.Page([EngineFixture.Message(1), invalid]),
                    CancellationToken.None
                ));
        Outcome<SearchPage> search = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);
        Checkpoint? checkpoint = await profile.ReadCheckpointAsync(EngineFixture.Account, "INBOX", CancellationToken.None);

        // Assert
        Assert.That(search.Value!.Messages, Is.Empty);
        Assert.That(checkpoint, Is.Null);
    }

    [Test]
    public async Task CommitAsync_WhenGenerationIncomplete_RemovesAbsentRowsOnlyOnCompletion()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]),
            CancellationToken.None
        );

        // Act
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(2)], "7:new", complete: false),
            CancellationToken.None
        );
        Outcome<SearchPage> partial = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([], "7:new"), CancellationToken.None);
        Outcome<SearchPage> complete = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);

        // Assert
        Assert.That(partial.Value!.Messages, Has.Length.EqualTo(2));
        Assert.That(complete.Value!.Messages.Select(item => item.Provider.Imap!.Uid), Is.EqualTo(new uint[] { 2 }));
    }

    [Test]
    public async Task QueueAsync_WhenReopened_PreservesIntentAndProjectionAndRejectsDuplicates()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        MutationIntent intent = new MutationIntent(
            new OperationId(Guid.NewGuid()),
            EngineFixture.Account,
            message.Id,
            message.Provider,
            MutationKind.SetRead,
            true,
            null
        );

        // Act
        bool queued = await profile.QueueAsync(intent, CancellationToken.None);
        bool duplicate = await profile.QueueAsync(intent, CancellationToken.None);
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        MessageSnapshot projection = await EngineFixture.FirstAsync(reopened);
        OperationEntry? claim = await reopened.ClaimAsync(
            EngineFixture.Account,
            DateTimeOffset.MaxValue,
            CancellationToken.None
        );
        await reopened.RecoverAsync(CancellationToken.None);
        bool staleResult = await reopened.FinishAsync(
            claim!,
            PendingState.Completed,
            Failure.None,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None
        );

        // Assert
        Assert.That(queued, Is.True);
        Assert.That(duplicate, Is.False);
        Assert.That(projection.Read, Is.True);
        Assert.That(projection.Pending, Is.EqualTo(PendingState.Queued));
        Assert.That(claim!.Intent, Is.EqualTo(intent));
        Assert.That(staleResult, Is.False);
    }

    [Test]
    public async Task ClaimAsync_WhenUidValidityChanges_DoesNotRedirectOldIntent()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
        MessageSnapshot old = await EngineFixture.FirstAsync(profile);
        await profile.QueueAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            old.Account,
            old.Id,
            old.Provider,
            MutationKind.Move,
            false,
            "Archive"
        ), CancellationToken.None);

        // Act
        await profile.CommitAsync(
            EngineFixture.Mailbox(8),
            EngineFixture.Page([EngineFixture.Message(1, 8)], "8:new"),
            CancellationToken.None
        );
        OperationEntry? claim = await profile.ClaimAsync(
            EngineFixture.Account,
            DateTimeOffset.MaxValue,
            CancellationToken.None
        );
        MessageSnapshot current = await EngineFixture.FirstAsync(profile);

        // Assert
        Assert.That(claim, Is.Null);
        Assert.That(current.Provider.Imap!.UidValidity, Is.EqualTo(8));
        Assert.That(current.Id, Is.Not.EqualTo(old.Id));
    }

    [Test]
    public async Task SearchAsync_WhenPageScopeChanges_RejectsTokenAndEvictionRemovesBodyTerms()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]),
            CancellationToken.None
        );
        MessageSnapshot first = await EngineFixture.FirstAsync(profile);
        BlobReference body = await EngineFixture.BlobAsync(profile, "body-only-word", protect: false);
        await profile.StoreBodyAsync(first.Account, first.Id, body, "bodyonlyword", CancellationToken.None);

        // Act
        Outcome<SearchPage> page = await profile.SearchAsync(new SearchRequest(
            first.Account,
            false,
            "",
            null,
            false,
            1
        ), CancellationToken.None);
        Outcome<SearchPage> wrong = await profile.SearchAsync(new SearchRequest(
            new AccountId(2),
            false,
            "",
            null,
            false,
            1,
            page.Value!.Continuation
        ), CancellationToken.None);
        Outcome<SearchPage> before = await profile.SearchAsync(new SearchRequest(
            first.Account,
            false,
            "bodyonlyword",
            null,
            false,
            10
        ), CancellationToken.None);
        await profile.EvictAsync(first.Account, 0, CancellationToken.None);
        Outcome<SearchPage> after = await profile.SearchAsync(new SearchRequest(
            first.Account,
            false,
            "bodyonlyword",
            null,
            false,
            10
        ), CancellationToken.None);

        // Assert
        Assert.That(wrong.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(before.Value!.Messages, Has.Length.EqualTo(1));
        Assert.That(after.Value!.Messages, Is.Empty);
        Assert.That((await EngineFixture.FirstAsync(profile)).Availability, Is.EqualTo(ContentAvailability.Evicted));
    }

    [Test]
    public async Task SearchAsync_WhenCachedMetadataIsAvailable_PreservesPagingAndBoundsPreviewWithoutFilteringAttachments()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([
            EngineFixture.Message(1), EngineFixture.Message(2), EngineFixture.Message(3)
        ]), CancellationToken.None);
        SearchRequest all = new SearchRequest(EngineFixture.Account, false, "", null, false, 10);
        MessageSnapshot[] messages = (await profile.SearchAsync(all, CancellationToken.None)).Value!.Messages.ToArray();
        foreach (MessageSnapshot message in messages.Skip(1))
        {
            await profile.StoreAttachmentsAsync(message, [new AttachmentSnapshot("1", "safe.txt", "text/plain", 3)], CancellationToken.None);
        }
        BlobReference body = await EngineFixture.BlobAsync(profile, "synthetic", protect: false);
        await profile.StoreBodyAsync(messages[1].Account, messages[1].Id, body, new string('x', 500), CancellationToken.None);
        SearchRequest paged = all with
        {
            Limit = 1
        };

        // Act
        SearchPage first = (await profile.SearchAsync(paged, CancellationToken.None)).Value!;
        SearchPage second = (await profile.SearchAsync(paged with
        {
            Continuation = first.Continuation
        }, CancellationToken.None)).Value!;
        SearchPage third = (await profile.SearchAsync(paged with
        {
            Continuation = second.Continuation
        }, CancellationToken.None)).Value!;
        Outcome<SearchPage> wrongFilter = await profile.SearchAsync(paged with
        {
            UnreadOnly = true,
            Continuation = first.Continuation
        }, CancellationToken.None);

        // Assert
        Assert.That(first.Messages.Single().Id, Is.EqualTo(messages[0].Id));
        Assert.That(first.Messages.Single().HasKnownAttachments, Is.False);
        Assert.That(second.Messages.Single().Id, Is.EqualTo(messages[1].Id));
        Assert.That(second.Messages.Single().HasKnownAttachments, Is.True);
        Assert.That(second.Messages.Single().CachedPreview, Has.Length.EqualTo(200));
        Assert.That(third.Messages.Single().Id, Is.EqualTo(messages[2].Id));
        Assert.That(third.Continuation, Is.Null);
        Assert.That(wrongFilter.Failure, Is.EqualTo(Failure.Validation));
    }

    [Test]
    public async Task SaveAsync_WhenNewLocalRevisionAndRemoteConflictArrive_PreservesAllVersions()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        Guid id = Guid.NewGuid();
        DraftSnapshot transmitted = await profile.SaveAsync(
            id,
            EngineFixture.Account,
            await EngineFixture.BlobAsync(profile, "first"),
            CancellationToken.None
        );
        BlobReference latest = await EngineFixture.BlobAsync(profile, "second");
        BlobReference remote = await EngineFixture.BlobAsync(profile, "conflicting");

        // Act
        await profile.SaveAsync(id, EngineFixture.Account, latest, CancellationToken.None);
        await profile.AcknowledgeAsync(
            transmitted,
            new RemoteDraftResult(null, "v1", null, false, Failure.None),
            CancellationToken.None
        );
        await profile.AcknowledgeAsync(
            transmitted with
            {
                Revision = 2
            },
            new RemoteDraftResult(null, "v2", remote, false, Failure.NeedsReview),
            CancellationToken.None
        );
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        DraftSnapshot? draft = await reopened.ReadAsync(id, EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(draft!.Revision, Is.EqualTo(2));
        Assert.That(draft.PushedRevision, Is.EqualTo(1));
        Assert.That(draft.Content, Is.EqualTo(latest));
        Assert.That(draft.Conflict, Is.True);
        Assert.That(Directory.GetFiles(Path.Combine(path, "objects"), "*.blob"), Has.Length.EqualTo(3));
    }

    [Test]
    public async Task OpenAsync_WhenSchemaIsNewer_RefusesWithoutResettingDatabase()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        await profile.DisposeAsync();
        string database = Path.Combine(path, "profile.db");
        using (SqliteConnection connection = new SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999;";
            command.ExecuteNonQuery();
        }

        // Act
        Outcome<SqliteProfile> result = await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NewerSchema));
        Assert.That(File.Exists(database), Is.True);
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Engine.Tests;
using Postfold.Storage;

namespace Postfold.Foundation.Tests;

public sealed class CommandReviewTests
{
    [Test]
    public async Task ResolveAsync_WhenReviewedRevisionIsStale_PreservesNewerDecisionAndEvidence()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        MutationIntent intent = new MutationIntent(new OperationId(Guid.NewGuid()), message.Account, message.Id,
            message.Provider, MutationKind.SetRead, true, null);
        await profile.QueueAsync(intent, CancellationToken.None);
        OperationEntry claimed = (await profile.ClaimAsync(message.Account, DateTimeOffset.UtcNow, CancellationToken.None))!;
        await profile.FinishAsync(claimed, PendingState.NeedsReview, Failure.NeedsReview, DateTimeOffset.UtcNow,
            CancellationToken.None);
        CommandSnapshot reviewed = (await profile.CommandsAsync(message.Account, CancellationToken.None))[0];

        // Act
        bool retry = await profile.ResolveAsync(intent.Id, message.Account, reviewed.Entry.Revision, true,
            CancellationToken.None);
        bool staleDismiss = await profile.ResolveAsync(intent.Id, message.Account, reviewed.Entry.Revision, false,
            CancellationToken.None);
        await using SqliteProfile reopened = (await SqliteProfile.OpenAsync(
            System.IO.Path.Combine(directory, "profile.db"), TimeProvider.System, CancellationToken.None)).Value!;
        CommandSnapshot persisted = (await reopened.CommandsAsync(message.Account, CancellationToken.None))[0];

        // Assert
        Assert.That(retry, Is.True);
        Assert.That(staleDismiss, Is.False);
        Assert.That(persisted.Entry.State, Is.EqualTo(PendingState.Queued));
        Assert.That(persisted.Entry.Evidence, Is.EqualTo(Failure.NeedsReview));
        Assert.That(persisted.Subject, Is.EqualTo(message.Subject));
        Assert.That(await reopened.CommandsAsync(new AccountId(2), CancellationToken.None), Is.Empty);
    }
}

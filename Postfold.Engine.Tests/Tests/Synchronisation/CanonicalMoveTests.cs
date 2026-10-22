using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Synchronisation;

public sealed class CanonicalMoveTests
{
    [TestCase(PendingState.Completed, 1)]
    [TestCase(PendingState.NeedsReview, 2)]
    public async Task FinishAsync_WhenImapMoveIsConfirmed_RemovesOnlyTheSourceWithoutInventingDestinationUids(
        PendingState state,
        int remaining
    )
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1), EngineFixture.Message(2)]), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        await profile.QueueAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            message.Account,
            message.Id,
            message.Provider,
            MutationKind.Move,
            false,
            "Trash"
        ), CancellationToken.None);
        OperationEntry claim = (await profile.ClaimAsync(message.Account, DateTimeOffset.MaxValue, CancellationToken.None))!;

        // Act
        await profile.FinishAsync(claim, state, Failure.None, DateTimeOffset.MaxValue, CancellationToken.None);
        SearchPage page = (await profile.SearchAsync(new SearchRequest(
            message.Account,
            false,
            "",
            null,
            false,
            10,
            InboxOnly: true
        ), CancellationToken.None)).Value!;

        // Assert
        Assert.That(page.Messages, Has.Length.EqualTo(remaining));
        Assert.That(page.Messages.All(item => item.Provider.Imap?.Mailbox == "INBOX"), Is.True);
        if (state == PendingState.Completed)
        {
            Assert.That(page.Messages.Any(item => item.Id == message.Id), Is.False);
        }
    }

    [TestCase("TRASH", PendingState.Completed, false, true)]
    [TestCase(null, PendingState.Completed, false, false)]
    [TestCase("INBOX", PendingState.Completed, true, false)]
    [TestCase("TRASH", PendingState.NeedsReview, true, false)]
    [TestCase("TRASH", PendingState.RetryScheduled, true, false)]
    public async Task FinishAsync_WhenCanonicalMoveIsAcknowledged_ProjectsOnlyConfirmedMembershipsWithoutAnotherSync(
        string? destination,
        PendingState state,
        bool inboxPresent,
        bool trashPresent
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        {
            MessageSnapshot message = EngineFixture.Message(1) with
            {
                Provider = new ProviderIdentity(EngineFixture.Account, "canonical", null)
            };
            MailboxSnapshot inbox = EngineFixture.Mailbox();
            await profile.CommitAccountAsync(new CanonicalPage(
                new Checkpoint(EngineFixture.Account, "$account", "generation", "cursor", 0, true),
                [inbox, inbox with { RemoteKey = "TRASH", Name = "Trash" }, inbox with { RemoteKey = "label", Name = "Label" }],
                [message],
                [new MembershipChange("canonical", "INBOX", true), new MembershipChange("canonical", "label", true)],
                [],
                true
            ), CancellationToken.None);
            MessageSnapshot local = (await profile.SearchAsync(new SearchRequest(
                EngineFixture.Account,
                false,
                "",
                null,
                false,
                10
            ), CancellationToken.None)).Value!.Messages.Single();
            await profile.QueueAsync(new MutationIntent(
                new OperationId(Guid.NewGuid()),
                local.Account,
                local.Id,
                local.Provider,
                MutationKind.Move,
                false,
                destination
            ), CancellationToken.None);
            OperationEntry claim = (await profile.ClaimAsync(local.Account, DateTimeOffset.MaxValue, CancellationToken.None))!;

            // Act
            bool completed = await profile.FinishAsync(claim, state, Failure.None, DateTimeOffset.MaxValue, CancellationToken.None);

            // Assert
            Assert.That(completed, Is.True);
            Assert.That(await ContainsAsync(profile, "INBOX"), Is.EqualTo(inboxPresent));
            Assert.That(await ContainsAsync(profile, "TRASH"), Is.EqualTo(trashPresent));
            Assert.That(await ContainsAsync(profile, "label"), Is.True);
            Assert.That(await profile.FinishAsync(
                claim,
                PendingState.Completed,
                Failure.None,
                DateTimeOffset.MaxValue,
                CancellationToken.None
            ), Is.False);
        }

        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        Assert.That(await ContainsAsync(reopened, "INBOX"), Is.EqualTo(inboxPresent));
        Assert.That(await ContainsAsync(reopened, "TRASH"), Is.EqualTo(trashPresent));
        Assert.That(await ContainsAsync(reopened, "label"), Is.True);
    }

    private static async Task<bool> ContainsAsync(
        SqliteProfile profile,
        string key
    )
    {
        MailboxSnapshot mailbox = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.Account == EngineFixture.Account && item.RemoteKey == key);
        return !(await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            mailbox.Id,
            false,
            10
        ), CancellationToken.None)).Value!.Messages.IsEmpty;
    }
}

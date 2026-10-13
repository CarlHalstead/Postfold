using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Search;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class MailboxUnreadCountTests
{
    [Test]
    public async Task MailboxUnreadCountsAsync_WhenMailExceedsTheLoadedPage_CountsOnlyItsAccountAndPendingReadProjection()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        AccountId second = new AccountId(2);
        ImmutableArray<MessageSnapshot> messages = Enumerable.Range(1, 300).Select(uid => EngineFixture.Message((uint)uid) with
        {
            Read = uid % 3 == 0
        }).ToImmutableArray();
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page(messages), CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(account: second), EngineFixture.Page(
            [EngineFixture.Message(1, account: second)], account: second
        ), CancellationToken.None);
        SearchPage page = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", null, true, 1),
            CancellationToken.None)).Value!;
        MessageSnapshot selected = page.Messages.Single();

        // Act
        ImmutableArray<MailboxUnreadCount> before = await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None);
        await profile.QueueAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            selected.Account,
            selected.Id,
            selected.Provider,
            MutationKind.SetRead,
            true,
            null
        ), CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> after = await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> other = await profile.MailboxUnreadCountsAsync(second, CancellationToken.None);

        // Assert
        Assert.That(before.Single().Count, Is.EqualTo(200));
        Assert.That(after.Single().Count, Is.EqualTo(199));
        Assert.That(after.Single().Account, Is.EqualTo(EngineFixture.Account));
        Assert.That(other.Single().Count, Is.EqualTo(1));
        Assert.That(other.Single().Account, Is.EqualTo(second));
        Assert.That(page.Continuation, Is.Not.Null);
    }

    [Test]
    public async Task MailboxUnreadCountsAsync_WhenUidValidityChanges_DoesNotCountStaleMembershipsAsCurrentUnread()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page(
            [EngineFixture.Message(1), EngineFixture.Message(2)]
        ), CancellationToken.None);
        long before = (await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None)).Single().Count;

        // Act
        await profile.CommitAsync(EngineFixture.Mailbox(8), EngineFixture.Page([], "8:g", complete: false), CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> counts = await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> unknown = await profile.MailboxUnreadCountsAsync(new AccountId(999), CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(8), EngineFixture.Page([EngineFixture.Message(1, validity: 8)], "8:g"),
            CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> recycled = await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None);
        await profile.DeactivateAccountAsync(EngineFixture.Account, CancellationToken.None);
        ImmutableArray<MailboxUnreadCount> inactive = await profile.MailboxUnreadCountsAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(before, Is.EqualTo(2));
        Assert.That(counts.Single().Count, Is.Zero);
        Assert.That(unknown, Is.Empty);
        Assert.That(recycled.Single().Count, Is.EqualTo(1));
        Assert.That(inactive, Is.Empty);
    }
}

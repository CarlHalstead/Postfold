using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
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

public sealed class DraftTrashTests
{
    [TestCase(PendingState.Completed, false)]
    [TestCase(PendingState.NeedsReview, true)]
    [TestCase(PendingState.RetryScheduled, true)]
    public async Task FinishAsync_WhenDraftMovesToTrash_ChangesVisibilityOnlyAfterAcknowledgement(
        PendingState state,
        bool draftVisible
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        {
            await SeedAsync(profile, EngineFixture.Account, false);
            MessageSnapshot local = await EngineFixture.FirstAsync(profile);
            await profile.QueueAsync(new MutationIntent(
                new OperationId(Guid.NewGuid()),
                local.Account,
                local.Id,
                local.Provider,
                MutationKind.Move,
                false,
                "TRASH"
            ), CancellationToken.None);
            OperationEntry claim = (await profile.ClaimAsync(local.Account, DateTimeOffset.MaxValue, CancellationToken.None))!;

            // Act
            await profile.FinishAsync(claim, state, Failure.None, DateTimeOffset.MaxValue, CancellationToken.None);

            // Assert
            await AssertVisibilityAsync(profile, EngineFixture.Account, draftVisible);
        }

        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await AssertVisibilityAsync(reopened, EngineFixture.Account, draftVisible);
    }

    [Test]
    public async Task SearchAsync_WhenCachedDraftIsTrashed_PreservesOtherAccountsAndRestoresWithoutLosingDraftMembership()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        AccountId other = new AccountId(2);
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        {
            await SeedAsync(profile, EngineFixture.Account, true);
            await SeedAsync(profile, other, false);

            // Act / Assert
            await AssertVisibilityAsync(profile, EngineFixture.Account, false);
            await AssertVisibilityAsync(profile, other, true);
        }

        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await AssertVisibilityAsync(reopened, EngineFixture.Account, false);
        await reopened.CommitAccountAsync(new CanonicalPage(
            new Checkpoint(EngineFixture.Account, "$account", "generation", "restored", 0, true),
            [],
            [],
            [new MembershipChange("draft", "TRASH", false)],
            [],
            false
        ), CancellationToken.None);
        await AssertVisibilityAsync(reopened, EngineFixture.Account, true);
        await AssertVisibilityAsync(reopened, other, true);
    }

    [Test]
    public async Task SearchAsync_WhenNewestDraftIsTrashed_FiltersBeforePagingAndRetainsOtherSearchScopes()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await SeedAsync(profile, EngineFixture.Account, true);
        await profile.CommitAccountAsync(new CanonicalPage(
            new Checkpoint(EngineFixture.Account, "$account", "generation", "more", 0, true),
            [],
            [Message(2), Message(3)],
            [new MembershipChange("2", "DRAFT", true), new MembershipChange("3", "DRAFT", true)],
            [],
            false
        ), CancellationToken.None);
        MailboxSnapshot drafts = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "DRAFT");
        SearchRequest request = new SearchRequest(EngineFixture.Account, false, "", drafts.Id, false, 1);

        // Act
        SearchPage first = (await profile.SearchAsync(request, CancellationToken.None)).Value!;
        SearchPage second = (await profile.SearchAsync(request with { Continuation = first.Continuation }, CancellationToken.None)).Value!;
        SearchPage all = (await profile.SearchAsync(request with { Folder = null, Limit = 10 }, CancellationToken.None)).Value!;

        // Assert
        Assert.That(first.Messages.Single().Provider.Key, Is.EqualTo("3"));
        Assert.That(first.Continuation, Is.Not.Null);
        Assert.That(second.Messages.Single().Provider.Key, Is.EqualTo("2"));
        Assert.That(second.Continuation, Is.Null);
        Assert.That(all.Messages.Select(item => item.Provider.Key), Is.EquivalentTo(new[] { "draft", "2", "3" }));
        Assert.That((await PageAsync(profile, EngineFixture.Account, "label")).Messages.Single().Provider.Key, Is.EqualTo("draft"));
        Assert.That((await PageAsync(profile, EngineFixture.Account, "TRASH")).Messages.Single().Provider.Key, Is.EqualTo("draft"));
    }

    private static MessageSnapshot Message(uint id)
    {
        return EngineFixture.Message(id) with
        {
            Provider = new ProviderIdentity(EngineFixture.Account, id.ToString(System.Globalization.CultureInfo.InvariantCulture), null)
        };
    }

    private static async Task SeedAsync(SqliteProfile profile, AccountId account, bool trashed)
    {
        MailboxSnapshot mailbox = EngineFixture.Mailbox(account: account);
        MessageSnapshot message = EngineFixture.Message(10, account: account) with
        {
            Provider = new ProviderIdentity(account, "draft", null)
        };
        await profile.CommitAccountAsync(new CanonicalPage(
            new Checkpoint(account, "$account", "generation", "cursor", 0, true),
            [
                mailbox with { RemoteKey = "DRAFT", Name = "Drafts", Role = MailboxRole.Drafts },
                mailbox with { RemoteKey = "TRASH", Name = "Trash", Role = MailboxRole.Trash },
                mailbox with { RemoteKey = "label", Name = "Label", Role = MailboxRole.Unresolved }
            ],
            [message],
            [new MembershipChange("draft", "DRAFT", true), new MembershipChange("draft", "TRASH", trashed), new MembershipChange("draft", "label", true)],
            [],
            true
        ), CancellationToken.None);
    }

    private static async Task AssertVisibilityAsync(SqliteProfile profile, AccountId account, bool draftVisible)
    {
        MailboxSnapshot drafts = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.Account == account && item.RemoteKey == "DRAFT");
        Assert.That((await PageAsync(profile, account, "DRAFT")).Messages.IsEmpty, Is.EqualTo(!draftVisible));
        Assert.That((await PageAsync(profile, account, "TRASH")).Messages.IsEmpty, Is.EqualTo(draftVisible));
        Assert.That((await PageAsync(profile, account, "label")).Messages, Has.Length.EqualTo(1));
        Assert.That((await profile.MailboxUnreadCountsAsync(account, CancellationToken.None)).Single(item => item.Folder == drafts.Id).Count, Is.EqualTo(draftVisible ? 1 : 0));
    }

    private static async Task<SearchPage> PageAsync(SqliteProfile profile, AccountId account, string key)
    {
        MailboxSnapshot mailbox = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.Account == account && item.RemoteKey == key);
        return (await profile.SearchAsync(new SearchRequest(account, false, "", mailbox.Id, false, 10), CancellationToken.None)).Value!;
    }
}

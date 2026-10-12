using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Synchronisation;

public sealed class CanonicalTests
{
    [Test]
    public async Task RunAsync_WhenAccountCursorExpires_StagesCompleteMembershipGenerationAndRetainsIntent()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        MessageSnapshot first = EngineFixture.Message(1) with
        {
            Provider = new ProviderIdentity(EngineFixture.Account, "canonical-one", null)
        };
        MessageSnapshot second = EngineFixture.Message(2) with
        {
            Provider = new ProviderIdentity(EngineFixture.Account, "canonical-two", null)
        };
        MailboxSnapshot inbox = EngineFixture.Mailbox();
        MailboxSnapshot label = inbox with
        {
            RemoteKey = "label",
            Name = "Synthetic label",
            Role = MailboxRole.Unresolved
        };
        CanonicalPage initial = new CanonicalPage(
            new Checkpoint(
    EngineFixture.Account,
    "$account",
    "original",
    "old-cursor",
    0,
    true
),
            [inbox, label],
            [first, second],
            [new MembershipChange("canonical-one", "INBOX", true), new MembershipChange("canonical-one", "label", true),
                new MembershipChange("canonical-two", "INBOX", true)],
            [],
            true
        );
        await profile.CommitAccountAsync(initial, CancellationToken.None);
        MessageSnapshot local = (await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None)).Value!.Messages.Single(message => message.Provider.Key == first.Provider.Key);
        MutationIntent intent = new MutationIntent(
            new OperationId(Guid.NewGuid()),
            local.Account,
            local.Id,
            local.Provider,
            MutationKind.SetRead,
            true,
            null
        );
        await profile.QueueAsync(intent, CancellationToken.None);
        ICanonicalMailboxReader reader = Substitute.For<ICanonicalMailboxReader>();
        reader.Account.Returns(EngineFixture.Account);
        CanonicalPage partial = new CanonicalPage(new Checkpoint(
            EngineFixture.Account,
            "$account",
            "replacement",
            "partial-cursor",
            0,
            false
        ), [], [first], [new MembershipChange("canonical-one", "label", true)], [], true);
        reader.ReadAsync(Arg.Any<Checkpoint?>(), 100, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(Outcome<CanonicalPage>.Fail(Failure.CursorExpired)),
            Task.FromResult(Outcome<CanonicalPage>.Success(partial))
        );
        CanonicalReconciliationWork work = new CanonicalReconciliationWork(reader, profile, 100);

        // Act
        await work.RunAsync(CancellationToken.None);
        Outcome<SearchPage> during = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);
        await profile.CommitAccountAsync(partial with
        {
            Checkpoint = partial.Checkpoint with
            {
                Complete = true,
                Cursor = "new-cursor"
            },
            Messages = [],
            Memberships = []
        }, CancellationToken.None);
        Outcome<SearchPage> complete = await profile.SearchAsync(new SearchRequest(
            EngineFixture.Account,
            false,
            "",
            null,
            false,
            10
        ), CancellationToken.None);
        OperationEntry? recovered = await profile.ClaimAsync(local.Account, DateTimeOffset.MaxValue, CancellationToken.None);

        // Assert
        Assert.That(during.Value!.Messages, Has.Length.EqualTo(2));
        Assert.That(complete.Value!.Messages, Has.Length.EqualTo(1));
        Assert.That(complete.Value.Messages[0].Read, Is.True);
        Assert.That(recovered!.Intent, Is.EqualTo(intent));
        await reader.Received(1).ReadAsync(null, 100, Arg.Any<CancellationToken>());
    }
}

using NUnit.Framework;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class RecipientHistoryTests
{
    [Test]
    public async Task RecognisedRecipientsAsync_WhenReopened_ReturnsDistinctLocalCorrespondentsOnlyForTheirAccount()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        {
            await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([
                EngineFixture.Message(1) with { Participants = "Friend <friend@synthetic.invalid>" },
                EngineFixture.Message(2) with { Participants = "Invalid address" },
                EngineFixture.Message(3) with { Participants = "Bad\r\n<friendbad@synthetic.invalid>" }
            ]), CancellationToken.None);
            AccountId other = new AccountId(2);
            await profile.CommitAsync(EngineFixture.Mailbox(account: other), EngineFixture.Page([
                EngineFixture.Message(1, account: other) with { Participants = "friendforeign@synthetic.invalid" }
            ], account: other), CancellationToken.None);
            FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "frozen MIME")) with
            {
                Recipients = ["FRIEND@synthetic.invalid", "friendtwo@synthetic.invalid", "user%tag@synthetic.invalid"]
            };
            await profile.QueueAsync(submission, CancellationToken.None);
        }

        // Act
        await using SqliteProfile reopened = (await SqliteProfile.OpenAsync(
            Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;
        ImmutableArray<string> addresses = await reopened.RecognisedRecipientsAsync(EngineFixture.Account, "FrIeNd", CancellationToken.None);
        ImmutableArray<string> literal = await reopened.RecognisedRecipientsAsync(EngineFixture.Account, "%", CancellationToken.None);

        // Assert
        Assert.That(addresses.Select(address => address.ToLowerInvariant()), Is.EquivalentTo(new[]
        {
            "friend@synthetic.invalid", "friendtwo@synthetic.invalid"
        }));
        Assert.That(literal, Is.EqualTo(new[] { "user%tag@synthetic.invalid" }));
        Assert.That(await reopened.RecognisedRecipientsAsync(new AccountId(2), "friend", CancellationToken.None),
            Is.EqualTo(new[] { "friendforeign@synthetic.invalid" }));
        Assert.That(await reopened.RecognisedRecipientsAsync(EngineFixture.Account, "", CancellationToken.None), Is.Empty);
    }

    [Test]
    public async Task RecognisedRecipientsAsync_WhenManyAddressesMatch_BoundsSuggestions()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        ImmutableArray<MessageSnapshot> messages = Enumerable.Range(1, 40).Select(index =>
            EngineFixture.Message((uint)index) with
            {
                Participants = "friend" + index + "@synthetic.invalid"
            }).ToImmutableArray();
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page(messages), CancellationToken.None);

        // Act
        ImmutableArray<string> addresses = await profile.RecognisedRecipientsAsync(EngineFixture.Account, "friend", CancellationToken.None);

        // Assert
        Assert.That(addresses, Has.Length.EqualTo(20));
        Assert.That(addresses[0], Is.EqualTo("friend40@synthetic.invalid"));
        Assert.That(addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(20));
    }
}

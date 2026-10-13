using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class AccountLifecycleTests
{
    [Test]
    public async Task RenameAccountAsync_WhenLabelChanges_PreservesEndpointsAndIdentityAcrossRestart()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        AccountConfiguration original;
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        {
            MailEndpoint endpoint = new MailEndpoint("mail.synthetic.invalid", 993, TlsMode.Implicit, "owner@synthetic.invalid", "secure-reference");
            await profile.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
            await profile.SaveIdentityAsync(EngineFixture.Account, new ComposeIdentity("owner@synthetic.invalid", "Signature"), CancellationToken.None);
            original = (await profile.AccountsAsync(CancellationToken.None))[0];

            // Act
            Assert.That(await profile.RenameAccountAsync(EngineFixture.Account, "  Personal mail  ", CancellationToken.None), Is.EqualTo(Failure.None));
            Assert.That(await profile.RenameAccountAsync(new AccountId(99), "Other", CancellationToken.None), Is.EqualTo(Failure.NotFound));
            Assert.That(await profile.RenameAccountAsync(EngineFixture.Account, " ", CancellationToken.None), Is.EqualTo(Failure.Validation));
        }

        // Assert
        await using SqliteProfile reopened = (await SqliteProfile.OpenAsync(
            System.IO.Path.Combine(directory, "profile.db"),
            TimeProvider.System,
            CancellationToken.None
        )).Value!;
        AccountConfiguration renamed = (await reopened.AccountsAsync(CancellationToken.None))[0];
        Assert.That(renamed.Name, Is.EqualTo("Personal mail"));
        Assert.That(renamed.Id, Is.EqualTo(original.Id));
        Assert.That(renamed.Incoming, Is.EqualTo(original.Incoming));
        Assert.That(renamed.Outgoing, Is.EqualTo(original.Outgoing));
        Assert.That(renamed.Identity, Is.EqualTo(original.Identity));
    }

    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public async Task DeactivateAccountAsync_WhenSubmissionNeedsAttention_RefusesAndPreservesFrozenEvidence(bool uncertain)
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "protected frozen MIME"));
        await profile.QueueAsync(submission, CancellationToken.None);
        if (uncertain)
        {
            await profile.ClaimAsync(EngineFixture.Account, CancellationToken.None);
            await profile.RecoverAsync(CancellationToken.None);
        }

        // Act
        Failure result = await profile.DeactivateAccountAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        OutboxEntry? retained = await ((IOutboxStore)profile).ReadAsync(submission.Id, EngineFixture.Account, CancellationToken.None);
        Assert.That(retained!.State, Is.EqualTo(uncertain ? SubmissionState.Uncertain : SubmissionState.Queued));
        Assert.That(retained.Submission.Mime, Is.EqualTo(submission.Mime));
    }

    [Test]
    public async Task DeactivateAccountAsync_WhenMutationIsPending_RefusesWithoutChangingProjection()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        MutationIntent intent = new MutationIntent(new OperationId(Guid.NewGuid()), EngineFixture.Account, message.Id,
            message.Provider, MutationKind.SetRead, true, null);
        await profile.QueueAsync(intent, CancellationToken.None);

        // Act
        Failure result = await profile.DeactivateAccountAsync(EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That((await EngineFixture.FirstAsync(profile)).Read, Is.True);
        Assert.That(await ((IOperationStore)profile).ReadNextAsync(EngineFixture.Account, CancellationToken.None), Is.Not.Null);
    }
}

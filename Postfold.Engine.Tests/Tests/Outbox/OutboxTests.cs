using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Outbox;

public sealed class OutboxTests
{
    [TestCase(SubmissionState.Queued)]
    [TestCase(SubmissionState.Uncertain)]
    [TestCase(SubmissionState.Completed)]
    public async Task QueueAsync_WhenSourceDraftWasAlreadyFrozen_RefusesDuplicateAcrossRestartAndPreservesContent(SubmissionState state)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "preserved original"));
        Guid draft = Guid.NewGuid();
        await profile.SaveAsync(draft, frozen.Account, frozen.Mime, CancellationToken.None);
        frozen = frozen with
        {
            SourceDraft = draft
        };
        await profile.QueueAsync(frozen, CancellationToken.None);
        if (state != SubmissionState.Queued)
        {
            OutboxEntry claim = (await profile.ClaimAsync(frozen.Id, frozen.Account, CancellationToken.None))!;
            await profile.FinishAsync(claim, new SubmissionOutcome(
                state == SubmissionState.Completed ? SubmissionState.Accepted : state,
                SubmissionPhase.Acknowledged,
                frozen.Recipients.Select(address => new RecipientEvidence(
                    address,
                    state == SubmissionState.Completed ? RecipientState.Accepted : RecipientState.Uncertain,
                    null
                )).ToImmutableArray(),
                state == SubmissionState.Completed ? Failure.None : Failure.NeedsReview
            ), CancellationToken.None);
            if (state == SubmissionState.Completed)
            {
                await profile.CompleteSentCopyAsync(frozen.Id, frozen.Account, claim.Revision + 1, CancellationToken.None);
            }
        }
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);

        // Act
        bool duplicate = await reopened.QueueAsync(frozen with
        {
            Id = Guid.NewGuid()
        }, CancellationToken.None);
        ImmutableArray<OutboxEntry> entries = await reopened.OutboxAsync(frozen.Account, CancellationToken.None);

        // Assert
        Assert.That(duplicate, Is.False);
        Assert.That(entries, Has.Length.EqualTo(1));
        Assert.That(entries[0].State, Is.EqualTo(state));
        Assert.That(await reopened.DraftsAsync(frozen.Account, CancellationToken.None), Is.Empty);
        Assert.That(await reopened.ReadAsync(draft, frozen.Account, CancellationToken.None), Is.Not.Null);
        Assert.That(await reopened.DeactivateAccountAsync(frozen.Account, CancellationToken.None),
            Is.EqualTo(state == SubmissionState.Completed ? Failure.None : Failure.NeedsReview));
    }

    [Test]
    public async Task QueueAsync_WhenOriginalWasCancelled_PreservesEditableDraftAndAllowsReviewedReplacement()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "preserved original"));
        Guid draft = Guid.NewGuid();
        await profile.SaveAsync(draft, frozen.Account, frozen.Mime, CancellationToken.None);
        frozen = frozen with
        {
            SourceDraft = draft
        };
        await profile.QueueAsync(frozen, CancellationToken.None);
        await profile.CancelQueuedAsync(frozen.Id, frozen.Account, 0, CancellationToken.None);

        // Act
        ImmutableArray<Postfold.Core.Models.Drafts.DraftSnapshot> editable = await profile.DraftsAsync(frozen.Account, CancellationToken.None);
        bool replacement = await profile.QueueAsync(frozen with
        {
            Id = Guid.NewGuid()
        }, CancellationToken.None);

        // Assert
        Assert.That(editable.Single().Id, Is.EqualTo(draft));
        Assert.That(replacement, Is.True);
        Assert.That(await profile.OutboxAsync(frozen.Account, CancellationToken.None), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task OutboxAsync_WhenHistoryExceedsPageBound_PrioritisesUnresolvedMail()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission waiting = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "preserved mail"));
        await profile.QueueAsync(waiting, CancellationToken.None);
        for (int index = 0; index < 200; index++)
        {
            FrozenSubmission history = waiting with
            {
                Id = Guid.NewGuid()
            };
            await profile.QueueAsync(history, CancellationToken.None);
            await profile.CancelQueuedAsync(history.Id, history.Account, 0, CancellationToken.None);
        }

        // Act
        ImmutableArray<OutboxEntry> entries = await profile.OutboxAsync(waiting.Account, CancellationToken.None);

        // Assert
        Assert.That(entries, Has.Length.EqualTo(200));
        Assert.That(entries[0].Submission.Id, Is.EqualTo(waiting.Id));
        Assert.That(entries[0].State, Is.EqualTo(SubmissionState.Queued));
    }

    [Test]
    public async Task RunAsync_WhenSendingSpecificMessage_PreservesOtherQueuedMailAndNeverResubmitsAfterRestart()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        FrozenSubmission older = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "older queued mail"));
        FrozenSubmission selected = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "newly composed mail"));
        await profile.QueueAsync(older, CancellationToken.None);
        await profile.QueueAsync(selected, CancellationToken.None);
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();
        submitter.SubmitAsync(Arg.Is<FrozenSubmission>(message => message.Id == selected.Id), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new SubmissionOutcome(
            SubmissionState.Accepted,
            SubmissionPhase.Acknowledged,
            selected.Recipients.Select(address => new RecipientEvidence(address, RecipientState.Accepted, 250)).ToImmutableArray(),
            Failure.None
        ));

        // Act
        await new OutboxWork(new AccountId(2), profile, profile.Blobs, submitter, selected.Id).RunAsync(CancellationToken.None);
        OutboxEntry? isolated = await ((IOutboxStore)profile).ReadAsync(selected.Id, selected.Account, CancellationToken.None);
        await new OutboxWork(selected.Account, profile, profile.Blobs, submitter, selected.Id).RunAsync(CancellationToken.None);
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        await reopened.RecoverAsync(CancellationToken.None);
        await new OutboxWork(selected.Account, reopened, reopened.Blobs, submitter, selected.Id).RunAsync(CancellationToken.None);
        OutboxEntry? preserved = await ((IOutboxStore)reopened).ReadAsync(older.Id, older.Account, CancellationToken.None);
        OutboxEntry? sent = await ((IOutboxStore)reopened).ReadAsync(selected.Id, selected.Account, CancellationToken.None);

        // Assert
        Assert.That(isolated!.State, Is.EqualTo(SubmissionState.Queued));
        Assert.That(preserved!.State, Is.EqualTo(SubmissionState.Queued));
        Assert.That(sent!.State, Is.EqualTo(SubmissionState.Accepted));
        await submitter.Received(1).SubmitAsync(Arg.Is<FrozenSubmission>(message => message.Id == selected.Id), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await submitter.DidNotReceive().SubmitAsync(Arg.Is<FrozenSubmission>(message => message.Id == older.Id), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [TestCase(SubmissionState.Accepted, RecipientState.Accepted)]
    [TestCase(SubmissionState.Uncertain, RecipientState.Uncertain)]
    [TestCase(SubmissionState.Rejected, RecipientState.Rejected)]
    [TestCase(SubmissionState.PartiallyAccepted, RecipientState.Accepted)]
    public async Task RunAsync_WhenTerminalEvidenceIsPersisted_DoesNotSubmitAgainAfterRestart(
        SubmissionState state,
        RecipientState recipientState
    )
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "frozen synthetic"));
        await profile.QueueAsync(submission, CancellationToken.None);
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();
        SubmissionOutcome outcome = new SubmissionOutcome(
            state,
            SubmissionPhase.Acknowledged,
            submission.Recipients.Select((address, index) => new RecipientEvidence(
    address,
    state == SubmissionState.PartiallyAccepted && index == 1 ? RecipientState.Rejected : recipientState,
    250
)).ToImmutableArray(),
            state == SubmissionState.Uncertain ? Failure.NeedsReview : Failure.None
        );
        submitter.SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(outcome));
        OutboxWork work = new OutboxWork(EngineFixture.Account, profile, profile.Blobs, submitter);

        // Act
        await work.RunAsync(CancellationToken.None);
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        await reopened.RecoverAsync(CancellationToken.None);
        await new OutboxWork(EngineFixture.Account, reopened, reopened.Blobs, submitter).RunAsync(CancellationToken.None);
        OutboxEntry? entry = await ((IOutboxStore)reopened).ReadAsync(submission.Id, submission.Account, CancellationToken.None);
        await using Stream bytes = await reopened.Blobs.OpenAsync(entry!.Submission.Mime, CancellationToken.None);
        using StreamReader reader = new StreamReader(bytes);
        string frozen = await reader.ReadToEndAsync();

        // Assert
        Assert.That(entry.State, Is.EqualTo(state));
        Assert.That(entry.Evidence!.Recipients, Is.EqualTo(outcome.Recipients));
        Assert.That(frozen, Is.EqualTo("frozen synthetic"));
        await submitter.Received(1).SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecoverAsync_WhenProcessDiesDuringSubmission_HoldsFrozenWorkAsUncertain()
    {
        // Arrange
        string path = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(path);
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "protected frozen"));
        await profile.QueueAsync(submission, CancellationToken.None);
        OutboxEntry? interrupted = await profile.ClaimAsync(submission.Account, CancellationToken.None);
        await profile.DisposeAsync();
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();

        // Act
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(path);
        await reopened.RecoverAsync(CancellationToken.None);
        await new OutboxWork(submission.Account, reopened, reopened.Blobs, submitter).RunAsync(CancellationToken.None);
        OutboxEntry? entry = await ((IOutboxStore)reopened).ReadAsync(submission.Id, submission.Account, CancellationToken.None);
        bool stale = await reopened.FinishAsync(
            interrupted!,
            new SubmissionOutcome(
    SubmissionState.Accepted,
    SubmissionPhase.Acknowledged,
    submission.Recipients.Select(address =>
                new RecipientEvidence(address, RecipientState.Accepted, 250)).ToImmutableArray(),
    Failure.None
),
            CancellationToken.None
        );

        // Assert
        Assert.That(entry!.State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(stale, Is.False);
        await submitter.DidNotReceive().SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WhenSentCopyFails_NeverReturnsToSubmitter()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "protected frozen"));
        await profile.QueueAsync(submission, CancellationToken.None);
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();
        submitter.SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SubmissionOutcome(
                SubmissionState.Accepted,
                SubmissionPhase.Acknowledged,
                submission.Recipients.Select(address =>
                    new RecipientEvidence(address, RecipientState.Accepted, 250)).ToImmutableArray(),
                Failure.None
            )));
        ISentCopyStore sent = Substitute.For<ISentCopyStore>();
        sent.SaveAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure.NeedsReview));
        OutboxWork outbox = new OutboxWork(submission.Account, profile, profile.Blobs, submitter);
        SentCopyWork sentCopy = new SentCopyWork(submission.Id, submission.Account, profile, profile.Blobs, sent);

        // Act
        await outbox.RunAsync(CancellationToken.None);
        await sentCopy.RunAsync(CancellationToken.None);
        await outbox.RunAsync(CancellationToken.None);
        OutboxEntry? entry = await ((IOutboxStore)profile).ReadAsync(submission.Id, submission.Account, CancellationToken.None);

        // Assert
        Assert.That(entry!.State, Is.EqualTo(SubmissionState.SentCopyPending));
        await submitter.Received(1).SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }
}

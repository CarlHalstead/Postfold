using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Outbox;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Outbox;

public sealed class OutboxTests
{
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

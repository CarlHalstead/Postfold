using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Engine.Tests;
using Postfold.Protocols;
using Postfold.Storage;
using Postfold.Sync;

namespace Postfold.Foundation.Tests;

public sealed class SentCopyReviewTests
{
    [Test]
    public async Task RetrySentCopyAsync_WhenReviewedRetrySucceeds_PreservesEvidenceAndNeverResubmits()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "frozen content"));
        await profile.QueueAsync(submission, CancellationToken.None);
        IMessageSubmitter submitter = Substitute.For<IMessageSubmitter>();
        submitter.SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new SubmissionOutcome(SubmissionState.Accepted, SubmissionPhase.Acknowledged,
                submission.Recipients.Select(address => new RecipientEvidence(address, RecipientState.Accepted, 250))
                    .ToImmutableArray(), Failure.None));
        ISentCopyStore sent = Substitute.For<ISentCopyStore>();
        sent.SaveAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure.NeedsReview), Task.FromResult(Failure.None));
        OutboxWork outbox = new OutboxWork(submission.Account, profile, profile.Blobs, submitter);
        SentCopyWork copy = new SentCopyWork(submission.Id, submission.Account, profile, profile.Blobs, sent);
        await outbox.RunAsync(CancellationToken.None);
        await copy.RunAsync(CancellationToken.None);
        OutboxEntry pending = (await profile.OutboxAsync(submission.Account, CancellationToken.None))[0];
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.StoreSentCopyAsync(submission.Id, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await copy.RunAsync(call.Arg<CancellationToken>());
            return Failure.None;
        });
        IAccountEngines engines = Substitute.For<IAccountEngines>();
        engines.Find(submission.Account).Returns(engine);
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        MailApplication application = new MailApplication(profile, profile, profile.Blobs, mime, engines, 1048576,
            mime, profile, profile, profile, profile);

        // Act
        Failure retried = await application.RetrySentCopyAsync(pending, CancellationToken.None);
        Failure stale = await application.RetrySentCopyAsync(pending, CancellationToken.None);
        await outbox.RunAsync(CancellationToken.None);
        OutboxEntry stored = (await profile.OutboxAsync(submission.Account, CancellationToken.None))[0];

        // Assert
        Assert.That(pending.State, Is.EqualTo(SubmissionState.SentCopyPending));
        Assert.That(retried, Is.EqualTo(Failure.None));
        Assert.That(stale, Is.EqualTo(Failure.NeedsReview));
        Assert.That(stored.State, Is.EqualTo(SubmissionState.Completed));
        Assert.That(stored.Evidence!.Recipients, Is.EqualTo(pending.Evidence!.Recipients));
        await submitter.Received(1).SubmitAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
        await sent.Received(2).SaveAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }
}

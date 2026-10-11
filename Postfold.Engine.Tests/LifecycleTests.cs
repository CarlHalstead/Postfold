using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Protocols;
using Postfold.Storage;
using Postfold.Sync;

namespace Postfold.Engine.Tests;

public sealed class LifecycleTests
{
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task QueueSubmissionAsync_WhenAccountReferencesAreInvalid_RefusesBeforeFreezing(
        bool foreignAttachment, bool missingDraft, bool missingPredecessor)
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        IMailboxReader reader = Substitute.For<IMailboxReader>();
        reader.Account.Returns(EngineFixture.Account);
        IMimePipeline mime = Substitute.For<IMimePipeline>();
        EnginePolicy policy = new EnginePolicy(false, false, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        await using MailAccountEngine engine = new MailAccountEngine(reader, Substitute.For<IMessageContentReader>(),
            null, null, null, Substitute.For<IMessageSubmitter>(), profile, profile, profile, profile, profile,
            profile.Blobs, mime, scheduler, policy, retry, TimeProvider.System,
            new EngineLimits(2, 8, 1, 100, 1048576, 100, 30, 0, 1048576), profile, []);
        ComposeContent content = new ComposeContent("sender@synthetic.invalid", ["recipient@synthetic.invalid"],
            "Synthetic", "body", DateTimeOffset.UnixEpoch, "id@synthetic.invalid",
            foreignAttachment ? [new ComposeAttachment("foreign.txt", "text/plain",
                new BlobReference(Guid.NewGuid(), new AccountId(2), 1, "synthetic", true))] : []);

        // Act
        Outcome<FrozenSubmission> result = await engine.QueueSubmissionAsync(content,
            missingDraft ? Guid.NewGuid() : null, missingPredecessor ? Guid.NewGuid() : null, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        await mime.DidNotReceive().FreezeAsync(Arg.Any<ComposeContent>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisposeAsync_WhenLocalSaveIsInProgress_WaitsForDurableRevision()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        BlobReference blob = await EngineFixture.BlobAsync(profile, "unsent local content");
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IBlobStore blobs = Substitute.For<IBlobStore>();
        blobs.WriteAsync(Arg.Any<AccountId>(), Arg.Any<Stream>(), Arg.Any<long>(), true,
            Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                entered.SetResult();
                await release.Task;
                return blob;
            });
        IMailboxReader reader = Substitute.For<IMailboxReader>();
        reader.Account.Returns(EngineFixture.Account);
        EnginePolicy policy = new EnginePolicy(false, false, false);
        RetryDelayPolicy retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        await using AccountScheduler scheduler = new AccountScheduler(2, 8, 1, TimeProvider.System, policy, retry);
        await using MailAccountEngine engine = new MailAccountEngine(reader, Substitute.For<IMessageContentReader>(),
            null, null, null, Substitute.For<IMessageSubmitter>(), profile, profile, profile, profile, profile, blobs,
            new MimePipeline(1048576, 100), scheduler, policy, retry, TimeProvider.System,
            new EngineLimits(2, 8, 1, 100, 1048576, 100, 30, 0, 1048576), profile, []);
        Guid id = Guid.NewGuid();
        using MemoryStream content = new MemoryStream();
        Task<Outcome<DraftSnapshot>> save = engine.SaveDraftAsync(id, content, CancellationToken.None);
        await entered.Task;

        // Act
        Task shutdown = engine.DisposeAsync().AsTask();
        bool waiting = !shutdown.IsCompleted;
        release.SetResult();
        Outcome<DraftSnapshot> saved = await save;
        await shutdown;
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        DraftSnapshot? restored = await reopened.ReadAsync(id, EngineFixture.Account, CancellationToken.None);

        // Assert
        Assert.That(waiting, Is.True);
        Assert.That(saved.IsSuccess, Is.True);
        Assert.That(restored, Is.EqualTo(saved.Value));
        Assert.That(restored!.Content.Protected, Is.True);
    }
}

using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using Postfold.Sync.Services;
using Postfold.Sync.Services.Work;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Synchronisation;

public sealed class MutationTests
{
    [Test]
    public async Task RunAsync_WhenTransientMutationIsRetried_HonoursDurableDeadline()
    {
        // Arrange
        ManualClock clock = new ManualClock();
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = (await SqliteProfile.OpenAsync(
            Path.Combine(directory, "profile.db"),
            clock,
            CancellationToken.None
        )).Value!;
        await profile.AddAccountAsync(EngineFixture.Account, "Synthetic", CancellationToken.None);
        await profile.CommitAsync(
            EngineFixture.Mailbox(),
            EngineFixture.Page([EngineFixture.Message(1)]),
            CancellationToken.None
        );
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        await profile.QueueAsync(new MutationIntent(
            new OperationId(Guid.NewGuid()),
            message.Account,
            message.Id,
            message.Provider,
            MutationKind.SetRead,
            true,
            null
        ), CancellationToken.None);
        IMailboxMutator mutator = Substitute.For<IMailboxMutator>();
        mutator.ApplyAsync(Arg.Any<MutationIntent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Failure.Transient), Task.FromResult(Failure.None));
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        retry.Delay(Arg.Any<int>()).Returns(TimeSpan.FromSeconds(7));
        await using AccountScheduler scheduler = new AccountScheduler(
            2,
            8,
            2,
            clock,
            new EnginePolicy(true, false, false),
            retry
        );

        // Act
        Task<Failure> running = scheduler.EnqueueAsync(
            message.Account,
            new MutationWork(message.Account, profile, mutator, clock, retry),
            WorkPriority.Interactive,
            CancellationToken.None
        );
        ManualTimer timer = await clock.TimerCreated;
        OperationEntry? waiting = await profile.ReadNextAsync(message.Account, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromTicks(1), timer);
        bool beforeDeadline = running.IsCompleted;
        clock.Advance(TimeSpan.FromTicks(1), timer);
        Failure result = await running;
        OperationEntry? remaining = await profile.ReadNextAsync(message.Account, CancellationToken.None);

        // Assert
        Assert.That(waiting!.State, Is.EqualTo(PendingState.RetryScheduled));
        Assert.That(waiting.Due, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(7)));
        Assert.That(beforeDeadline, Is.False);
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(remaining, Is.Null);
        await mutator.Received(2).ApplyAsync(Arg.Any<MutationIntent>(), Arg.Any<CancellationToken>());
    }
}

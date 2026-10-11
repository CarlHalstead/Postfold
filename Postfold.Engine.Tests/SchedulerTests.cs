using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Sync;

namespace Postfold.Engine.Tests;

public sealed class SchedulerTests
{
    [Test]
    public async Task EnqueueAsync_WhenTransientFailureHasDeadline_RetriesOnlyAtInjectedTime()
    {
        // Arrange
        ManualClock time = new ManualClock();
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        TimeSpan deadline = TimeSpan.FromSeconds(7);
        retry.Delay(1).Returns(deadline);
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 2, time, policy, retry);
        IAccountWork work = Substitute.For<IAccountWork>();
        work.RunAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(Failure.Transient), Task.FromResult(Failure.None));

        // Act
        Task<Failure> result = scheduler.EnqueueAsync(new AccountId(1), work, WorkPriority.Reconciliation, CancellationToken.None);
        ManualTimer timer = await time.TimerCreated.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(deadline - TimeSpan.FromTicks(1), timer);
        bool beforeDeadline = result.IsCompleted;
        time.Advance(TimeSpan.FromTicks(1), timer);
        Failure completed = await result.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(beforeDeadline, Is.False);
        Assert.That(completed, Is.EqualTo(Failure.None));
        await work.Received(2).RunAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueAsync_WhenOneAccountIsSlow_OtherAccountsProgressWithoutOverlappingOwnership()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        policy.AllowOptionalWork.Returns(true);
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, retry);
        TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAccountWork slow = Substitute.For<IAccountWork>();
        slow.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult(true);
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return Failure.None;
        });
        IAccountWork sameAccount = Substitute.For<IAccountWork>();
        sameAccount.RunAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Failure.None));
        IAccountWork other = Substitute.For<IAccountWork>();
        other.RunAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(Failure.None));

        // Act
        Task<Failure> first = scheduler.EnqueueAsync(new AccountId(1), slow, WorkPriority.Reconciliation, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> second = scheduler.EnqueueAsync(new AccountId(1), sameAccount, WorkPriority.Interactive, CancellationToken.None);
        Failure otherResult = await scheduler.EnqueueAsync(new AccountId(2), other, WorkPriority.Reconciliation,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        bool ownershipHeld = !second.IsCompleted;
        release.TrySetResult(true);
        await Task.WhenAll(first, second);

        // Assert
        Assert.That(otherResult, Is.EqualTo(Failure.None));
        Assert.That(ownershipHeld, Is.True);
        await sameAccount.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DisposeAsync_WhenOwnedWorkIsRunning_CancelsAndObservesCompletion()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        AccountScheduler scheduler = new AccountScheduler(2, 10, 3, TimeProvider.System, policy, retry);
        TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAccountWork work = Substitute.For<IAccountWork>();
        work.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Failure.None;
        });

        // Act
        Task<Failure> owned = scheduler.EnqueueAsync(new AccountId(1), work, WorkPriority.Interactive, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scheduler.DisposeAsync();

        // Assert
        Assert.That(owned.IsCanceled, Is.True);
        Assert.Throws<ObjectDisposedException>(() => scheduler.EnqueueAsync(new AccountId(2), work,
            WorkPriority.Reconciliation, CancellationToken.None));
    }

    [TestCase(Failure.NeedsAuthentication)]
    [TestCase(Failure.Validation)]
    [TestCase(Failure.NeedsReview)]
    public async Task EnqueueAsync_WhenFailureRequiresAction_DoesNotRetry(Failure failure)
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 5, TimeProvider.System, policy, retry);
        IAccountWork work = Substitute.For<IAccountWork>();
        work.RunAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(failure));

        // Act
        Failure result = await scheduler.EnqueueAsync(new AccountId(1), work, WorkPriority.Reconciliation,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(failure));
        await work.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnqueueAsync_WhenOptionalWorkIsDisallowed_DoesNotRunPrefetch()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        policy.AllowOptionalWork.Returns(false);
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, retry);
        IAccountWork work = Substitute.For<IAccountWork>();

        // Act
        Failure result = await scheduler.EnqueueAsync(new AccountId(1), work, WorkPriority.Prefetch,
            CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Unavailable));
        await work.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
    }
}

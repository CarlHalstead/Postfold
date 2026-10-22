using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Engine.Tests.Fakes;
using Postfold.Sync.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Scheduling;

public sealed class SchedulerTests
{
    [Test]
    public async Task EnqueueAsync_WhenSerialWorkersAreSyncing_RunsOneIndependentReadWithoutOverlappingSerialWork()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, Substitute.For<IRetryDelayPolicy>());
        TaskCompletionSource firstSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAccountWork syncing = Substitute.For<IAccountWork>();
        int started = 0;
        syncing.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            (Interlocked.Increment(ref started) == 1 ? firstSync : secondSync).TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return Failure.None;
        });
        AccountId account = new AccountId(1);
        Task<Failure> first = scheduler.EnqueueAsync(account, syncing, WorkPriority.Reconciliation, CancellationToken.None);
        Task<Failure> second = scheduler.EnqueueAsync(new AccountId(2), syncing, WorkPriority.Reconciliation, CancellationToken.None);
        await Task.WhenAll(firstSync.Task, secondSync.Task).WaitAsync(TimeSpan.FromSeconds(10));
        IAccountWork mutation = Substitute.For<IAccountWork>();
        mutation.RunAsync(Arg.Any<CancellationToken>()).Returns(Failure.None);
        Task<Failure> queuedMutation = scheduler.EnqueueAsync(account, mutation, WorkPriority.Interactive, CancellationToken.None);
        IConcurrentAccountWork reading = Substitute.For<IConcurrentAccountWork>();
        reading.RunAsync(Arg.Any<CancellationToken>()).Returns(Failure.None);

        // Act
        Failure result = await scheduler.EnqueueAsync(account, reading, WorkPriority.Interactive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        bool serialStillHeld = !queuedMutation.IsCompleted && !first.IsCompleted && !second.IsCompleted;
        release.TrySetResult();
        await Task.WhenAll(first, second, queuedMutation).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(serialStillHeld, Is.True);
        await reading.Received(1).RunAsync(Arg.Any<CancellationToken>());
        await mutation.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SuspendAsync_WhenBothAccountLanesAreActive_CancelsAndDrainsBoth(bool dispose)
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, Substitute.For<IRetryDelayPolicy>());
        AccountId account = new AccountId(1);
        TaskCompletionSource syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAccountWork syncing = Substitute.For<IAccountWork>();
        syncing.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            syncEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Failure.None;
        });
        IConcurrentAccountWork reading = Substitute.For<IConcurrentAccountWork>();
        reading.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            readEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Failure.None;
        });
        Task<Failure> sync = scheduler.EnqueueAsync(account, syncing, WorkPriority.Reconciliation, CancellationToken.None);
        await syncEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> read = scheduler.EnqueueAsync(account, reading, WorkPriority.Interactive, CancellationToken.None);
        await readEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        if (dispose)
        {
            await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            await scheduler.SuspendAsync(account).WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Assert
        Assert.That(sync.IsCanceled, Is.True);
        Assert.That(read.IsCanceled, Is.True);
    }

    [Test]
    public async Task PauseAsync_WhenIndependentReadIsActive_DrainsItAndPreservesSingleReadCapacity()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, Substitute.For<IRetryDelayPolicy>());
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IConcurrentAccountWork reading = Substitute.For<IConcurrentAccountWork>();
        CancellationToken observed = default;
        reading.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<CancellationToken>();
            entered.TrySetResult();
            await release.Task.WaitAsync(observed);
            return Failure.None;
        });
        Task<Failure> active = scheduler.EnqueueAsync(new AccountId(1), reading, WorkPriority.Interactive, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> queued = scheduler.EnqueueAsync(new AccountId(2), reading, WorkPriority.Interactive, CancellationToken.None);

        // Act
        Task paused = scheduler.PauseAsync(CancellationToken.None);
        bool pausedForRead = !paused.IsCompleted;
        release.TrySetResult();
        await paused.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(pausedForRead, Is.True);
        Assert.That(observed.IsCancellationRequested, Is.False);
        Assert.That(await active, Is.EqualTo(Failure.None));
        Assert.That(await queued, Is.EqualTo(Failure.PolicyBlocked));
        await reading.Received(1).RunAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PauseAsync_WhenWorkIsActive_ObservesWithoutCancellingAndNeverRestoresDiscardedCallbacks()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, retry);
        AccountId account = new AccountId(1);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        IAccountWork active = Substitute.For<IAccountWork>();
        active.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<CancellationToken>();
            entered.SetResult();
            await release.Task;
            return Failure.None;
        });
        IAccountWork queued = Substitute.For<IAccountWork>();
        queued.RunAsync(Arg.Any<CancellationToken>()).Returns(Failure.None);
        Task<Failure> running = scheduler.EnqueueAsync(account, active, WorkPriority.Interactive, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> waiting = scheduler.EnqueueAsync(account, queued, WorkPriority.Interactive, CancellationToken.None);

        // Act
        Task paused = scheduler.PauseAsync(CancellationToken.None);
        Failure denied = await scheduler.EnqueueAsync(new AccountId(2), queued, WorkPriority.Interactive, CancellationToken.None);
        bool waitsForActive = !paused.IsCompleted;
        release.SetResult();
        await paused.WaitAsync(TimeSpan.FromSeconds(10));
        scheduler.ResumeAll();

        // Assert
        Assert.That(waitsForActive, Is.True);
        Assert.That(observed.IsCancellationRequested, Is.False);
        Assert.That(await running, Is.EqualTo(Failure.None));
        Assert.That(await waiting, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(denied, Is.EqualTo(Failure.PolicyBlocked));
        await queued.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
        Assert.That(await scheduler.EnqueueAsync(account, queued, WorkPriority.Interactive, CancellationToken.None), Is.EqualTo(Failure.None));
    }

    [Test]
    public async Task SuspendAsync_WhenAccountHasRunningAndQueuedWork_StopsOnlyThatAccountAndCanResume()
    {
        // Arrange
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 10, 1, TimeProvider.System, policy, retry);
        AccountId account = new AccountId(1);
        TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAccountWork slow = Substitute.For<IAccountWork>();
        slow.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return Failure.None;
        });
        IAccountWork queued = Substitute.For<IAccountWork>();
        queued.RunAsync(Arg.Any<CancellationToken>()).Returns(Failure.None);
        Task<Failure> active = scheduler.EnqueueAsync(account, slow, WorkPriority.Reconciliation, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> waiting = scheduler.EnqueueAsync(account, queued, WorkPriority.Interactive, CancellationToken.None);

        // Act
        await scheduler.SuspendAsync(account).WaitAsync(TimeSpan.FromSeconds(10));
        Failure blocked = await scheduler.EnqueueAsync(account, queued, WorkPriority.Interactive, CancellationToken.None);
        Failure other = await scheduler.EnqueueAsync(new AccountId(2), queued, WorkPriority.Interactive, CancellationToken.None);
        scheduler.Resume(account);
        Failure resumed = await scheduler.EnqueueAsync(account, queued, WorkPriority.Interactive, CancellationToken.None);

        // Assert
        Assert.That(active.IsCanceled, Is.True);
        Assert.That(await waiting, Is.EqualTo(Failure.Unavailable));
        Assert.That(blocked, Is.EqualTo(Failure.Unavailable));
        Assert.That(other, Is.EqualTo(Failure.None));
        Assert.That(resumed, Is.EqualTo(Failure.None));
        await queued.Received(2).RunAsync(Arg.Any<CancellationToken>());
    }

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
            Task.FromResult(Failure.Transient),
            Task.FromResult(Failure.None)
        );

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
        Failure otherResult = await scheduler.EnqueueAsync(
            new AccountId(2),
            other,
            WorkPriority.Reconciliation,
            CancellationToken.None
        ).WaitAsync(TimeSpan.FromSeconds(10));
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
        Assert.Throws<ObjectDisposedException>(() => scheduler.EnqueueAsync(
            new AccountId(2),
            work,
            WorkPriority.Reconciliation,
            CancellationToken.None
        ));
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
        Failure result = await scheduler.EnqueueAsync(
            new AccountId(1),
            work,
            WorkPriority.Reconciliation,
            CancellationToken.None
        );

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
        Failure result = await scheduler.EnqueueAsync(
            new AccountId(1),
            work,
            WorkPriority.Prefetch,
            CancellationToken.None
        );

        // Assert
        Assert.That(result, Is.EqualTo(Failure.Unavailable));
        await work.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
    }
}

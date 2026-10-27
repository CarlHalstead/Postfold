using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Desktop.Services;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Synchronisation;

public sealed class BackgroundSynchronisationTests
{
    [Test]
    [TestCase(3)]
    [TestCase(60)]
    public async Task RunAsync_WhenTheIntervalIsConfigured_UsesThatDelayBetweenCompletedPasses(int seconds)
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(new AccountId(1), "Synthetic", null, null)));
        UpdatePolicyClock clock = new UpdatePolicyClock();
        using CancellationTokenSource lifetime = new CancellationTokenSource();
        int passes = 0;
        TaskCompletionSource<bool> second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MailSynchronisationLoop loop = new MailSynchronisationLoop(application, clock, () => seconds);
        Task running = loop.RunAsync((id, failure, token) =>
        {
            if (Interlocked.Increment(ref passes) == 2)
            {
                second.SetResult(true);
            }
            return Task.CompletedTask;
        }, lifetime.Token);
        try
        {
            // Act
            clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Assert.That(passes, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.That(passes, Is.EqualTo(2));
        }
        finally
        {
            await lifetime.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task RunAsync_WhenAPassIsPending_DoesNotOverlapAndCancelsTheActiveSyncOnShutdown()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        AccountId account = new AccountId(1);
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(account, "Synthetic", null, null)
        ));
        TaskCompletionSource<Failure> pending = new TaskCompletionSource<Failure>(TaskCreationOptions.RunContinuationsAsynchronously);
        application.SynchroniseScheduledAsync(account, Arg.Any<CancellationToken>()).Returns(call => pending.Task.WaitAsync(call.Arg<CancellationToken>()));
        UpdatePolicyClock clock = new UpdatePolicyClock();
        using CancellationTokenSource lifetime = new CancellationTokenSource();
        int refreshed = 0;
        MailSynchronisationLoop loop = new MailSynchronisationLoop(application, clock);

        // Act
        Task running = loop.RunAsync((id, failure, token) =>
        {
            refreshed++;
            return Task.CompletedTask;
        }, lifetime.Token);
        try
        {
            clock.Advance(TimeSpan.FromMinutes(10));

            // Assert
            Assert.That(running.IsCompleted, Is.False);
            Assert.That(refreshed, Is.Zero);
            Assert.That(application.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IMailApplication.SynchroniseScheduledAsync)), Is.EqualTo(1));
        }
        finally
        {
            await lifetime.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task RunAsync_WhenPollingAfterACompletedPass_RefreshesAgainAndDrainsOnCancellation()
    {
        // Arrange
        IMailApplication application = Substitute.For<IMailApplication>();
        AccountConfiguration account = new AccountConfiguration(new AccountId(1), "Synthetic", null, null);
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(account));
        application.SynchroniseScheduledAsync(account.Id, Arg.Any<CancellationToken>()).Returns(Failure.None, Failure.Transient);
        UpdatePolicyClock clock = new UpdatePolicyClock();
        using CancellationTokenSource lifetime = new CancellationTokenSource();
        int passes = 0;
        TaskCompletionSource<Failure> second = new TaskCompletionSource<Failure>(TaskCreationOptions.RunContinuationsAsynchronously);
        MailSynchronisationLoop loop = new MailSynchronisationLoop(application, clock);

        // Act
        Task running = loop.RunAsync((id, failure, token) =>
        {
            if (Interlocked.Increment(ref passes) == 2)
            {
                second.SetResult(failure);
            }
            return Task.CompletedTask;
        }, lifetime.Token);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.That(passes, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            Failure repeated = await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.That(repeated, Is.EqualTo(Failure.Transient));
            await application.Received(2).SynchroniseScheduledAsync(account.Id, Arg.Any<CancellationToken>());
            await application.DidNotReceiveWithAnyArgs().SynchroniseAsync(default, default);
            await application.DidNotReceiveWithAnyArgs().ProcessOutboxAsync(default, default);
        }
        finally
        {
            await lifetime.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

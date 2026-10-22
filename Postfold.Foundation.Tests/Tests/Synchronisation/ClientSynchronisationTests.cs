using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Synchronisation;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Synchronisation;

public sealed class ClientSynchronisationTests
{
    [Test]
    public async Task SynchroniseAsync_WhenManualAndBackgroundRequestsOverlap_SerialisesTheAccountAndCancelsAWaitingRequest()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray.Create(EngineFixture.Mailbox()));
        engine.Capabilities.Returns(new Capabilities(false, false, false, true, false, false));
        TaskCompletionSource<Failure> reconciliation = new TaskCompletionSource<Failure>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ReconcileAsync("INBOX", Arg.Any<CancellationToken>()).Returns(reconciliation.Task);
        IAccountEngines engines = Substitute.For<IAccountEngines>();
        engines.Find(EngineFixture.Account).Returns(engine);
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        MailApplication application = new MailApplication(
            profile,
            profile,
            profile.Blobs,
            mime,
            engines,
            1048576,
            mime,
            profile,
            profile,
            profile,
            profile
        );
        using CancellationTokenSource waiting = new CancellationTokenSource();

        // Act
        Task<Failure> background = application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);
        Task<Failure> manual = application.SynchroniseAsync(EngineFixture.Account, waiting.Token);
        try
        {
            Assert.That(engine.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IMailAccountEngine.ReconcileAsync)), Is.EqualTo(1));
            Assert.That(manual.IsCompleted, Is.False);
            await waiting.CancelAsync();

            // Assert
            Assert.That(async () => await manual, Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            reconciliation.TrySetResult(Failure.None);
            await background.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.That(await application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None), Is.EqualTo(Failure.None));
        await engine.Received(2).ReconcileAsync("INBOX", Arg.Any<CancellationToken>());
    }

    [Test]
    [TestCase(Failure.None, Failure.NeedsReview, 1)]
    [TestCase(Failure.NeedsReview, Failure.None, 0)]
    public async Task SynchroniseAsync_WhenReconciliationFinishes_DrainsCommandsOnlyAfterSafeReconciliation(
        Failure reconciliation,
        Failure commands,
        int commandCalls
    )
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic", "fixture");
        await profile.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray.Create(EngineFixture.Mailbox()));
        engine.Capabilities.Returns(new Capabilities(false, false, false, true, false, false));
        engine.ReconcileAsync("INBOX", Arg.Any<CancellationToken>()).Returns(reconciliation);
        engine.ProcessMutationsAsync(Arg.Any<CancellationToken>()).Returns(commands);
        IAccountEngines engines = Substitute.For<IAccountEngines>();
        engines.Find(EngineFixture.Account).Returns(engine);
        MimePipeline mime = new MimePipeline(1048576, 100, profile.Blobs);
        MailApplication application = new MailApplication(
            profile,
            profile,
            profile.Blobs,
            mime,
            engines,
            1048576,
            mime,
            profile,
            profile,
            profile,
            profile
        );

        // Act
        Failure result = await application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);
        AccountConfiguration account = (await profile.AccountsAsync(CancellationToken.None))[0];

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(account.Capabilities, Is.EqualTo(reconciliation == Failure.None ? engine.Capabilities : null));
        await engine.Received(commandCalls).ProcessMutationsAsync(Arg.Any<CancellationToken>());
    }
}

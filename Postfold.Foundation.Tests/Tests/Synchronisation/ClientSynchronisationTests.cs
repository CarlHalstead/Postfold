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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Synchronisation;

public sealed class ClientSynchronisationTests
{
    [Test]
    [TestCase(false)]
    [TestCase(true)]
    public void SynchroniseAsync_WhenLocalIoFails_ReportsTransientFailureToTheWindow(bool scheduled)
    {
        // Arrange
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray.Create(EngineFixture.Mailbox()));
        engine.ReconcileAsync("INBOX", Arg.Any<CancellationToken>()).Returns(Task.FromException<Failure>(new IOException()));
        IAccountEngines engines = Substitute.For<IAccountEngines>();
        engines.Find(EngineFixture.Account).Returns(engine);
        MailApplication application = new MailApplication(
            Substitute.For<IClientCatalog>(),
            Substitute.For<ILocalMailQuery>(),
            Substitute.For<IBlobStore>(),
            Substitute.For<IMimePipeline>(),
            engines,
            1048576,
            Substitute.For<IMimeDraftReader>(),
            Substitute.For<IOutboxStore>(),
            Substitute.For<IOperationStore>(),
            Substitute.For<IAccountCapabilityStore>(),
            Substitute.For<ISynchronisationStore>()
        );
        List<SynchronisationUpdate> updates = [];
        application.SynchronisationChanged += (_, update) => updates.Add(update);

        // Act and assert
        Assert.That(async () => await (scheduled ?
            application.SynchroniseScheduledAsync(EngineFixture.Account, CancellationToken.None) :
            application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None)), Throws.InstanceOf<IOException>());
        Assert.That(updates, Is.EqualTo(new[]
        {
            new SynchronisationUpdate(EngineFixture.Account, true, false, ShowActivity: !scheduled),
            new SynchronisationUpdate(EngineFixture.Account, false, true, Failure.Transient, !scheduled)
        }));
    }

    [Test]
    public async Task SynchroniseAsync_WhenAccountUsesMailboxes_ReconcilesEachSelectableMailbox()
    {
        // Arrange
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray.Create(
            EngineFixture.Mailbox(),
            EngineFixture.Mailbox() with { RemoteKey = "Archive" },
            EngineFixture.Mailbox() with { RemoteKey = "Unavailable", Selectable = false }
        ));
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

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        await engine.Received(1).ReconcileAsync("INBOX", Arg.Any<CancellationToken>());
        await engine.Received(1).ReconcileAsync("Archive", Arg.Any<CancellationToken>());
        await engine.DidNotReceive().ReconcileAsync("Unavailable", Arg.Any<CancellationToken>());
    }

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
        List<SynchronisationUpdate> updates = [];
        application.SynchronisationChanged += (_, update) => updates.Add(update);

        // Act
        Task<Failure> background = application.SynchroniseScheduledAsync(EngineFixture.Account, CancellationToken.None);
        Task<Failure> manual = application.SynchroniseAsync(EngineFixture.Account, waiting.Token);
        engine.MetadataCommitted += Raise.Event();
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
        Assert.That(updates, Is.EqualTo(new[]
        {
            new SynchronisationUpdate(EngineFixture.Account, true, false, ShowActivity: false),
            new SynchronisationUpdate(EngineFixture.Account, true, true, ShowActivity: false),
            new SynchronisationUpdate(EngineFixture.Account, false, true, ShowActivity: false)
        }));
        engine.MetadataCommitted += Raise.Event();
        Assert.That(updates, Has.Count.EqualTo(3));
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

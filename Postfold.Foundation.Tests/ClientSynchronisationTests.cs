using System.Collections.Immutable;
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

public sealed class ClientSynchronisationTests
{
    [Test]
    [TestCase(Failure.None, Failure.NeedsReview, 1)]
    [TestCase(Failure.NeedsReview, Failure.None, 0)]
    public async Task SynchroniseAsync_WhenReconciliationFinishes_DrainsCommandsOnlyAfterSafeReconciliation(
        Failure reconciliation, Failure commands, int commandCalls)
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
        MailApplication application = new MailApplication(profile, profile, profile.Blobs, mime, engines, 1048576,
            mime, profile, profile, profile, profile);

        // Act
        Failure result = await application.SynchroniseAsync(EngineFixture.Account, CancellationToken.None);
        AccountConfiguration account = (await profile.AccountsAsync(CancellationToken.None))[0];

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(account.Capabilities, Is.EqualTo(reconciliation == Failure.None ? engine.Capabilities : null));
        await engine.Received(commandCalls).ProcessMutationsAsync(Arg.Any<CancellationToken>());
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;
using Postfold.Engine.Tests;
using Postfold.Protocols;

namespace Postfold.Foundation.Tests;

public sealed class AccountSetupTests
{
    [Test]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task RestoreCredentialsAsync_WhenStorageChoiceIsExplicit_UsesOnlyChosenStore(
        bool sessionOnly, bool storageFails)
    {
        // Arrange
        AccountId account = new AccountId(7);
        IAccountProvisioner host = Substitute.For<IAccountProvisioner>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        host.Catalog.Returns(catalog);
        MailEndpoint incoming = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "incoming", "incoming-key");
        MailEndpoint outgoing = new MailEndpoint("synthetic.invalid", 465, TlsMode.Implicit, "outgoing", "outgoing-key");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(account, "Preserved account", incoming, outgoing)));
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        ICredentialStore session = Substitute.For<ICredentialStore>();
        ICredentialStore chosen = sessionOnly ? session : secure;
        chosen.WriteAsync(Arg.Any<string>(), Arg.Any<Credential>(), Arg.Any<CancellationToken>()).Returns(
            storageFails ? Outcome<bool>.Fail(Failure.Unavailable) : Outcome<bool>.Success(true));
        AccountSetup setup = new AccountSetup(host, secure, session);

        // Act
        Failure result = await setup.RestoreCredentialsAsync(account, "incoming-secret", "outgoing-secret",
            sessionOnly, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(storageFails ? Failure.CredentialStorageUnavailable : Failure.None));
        await chosen.Received(1).WriteAsync("incoming-key", Arg.Is<Credential>(value => value.Secret == "incoming-secret"),
            Arg.Any<CancellationToken>());
        await chosen.Received(1).WriteAsync("outgoing-key", Arg.Is<Credential>(value => value.Secret == "outgoing-secret"),
            Arg.Any<CancellationToken>());
        await (sessionOnly ? secure : session).DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        await host.Received(storageFails ? 0 : 1).ReplaceCredentialsAsync(account, chosen, Arg.Any<CancellationToken>());
        await host.DidNotReceiveWithAnyArgs().ConnectAccountAsync(default, default!, default!, default!, default!, default,
            default);
    }

    [Test]
    public async Task AddAsync_WhenNetworkPolicyBlocks_ReportsPolicyWithoutStoringCredentials()
    {
        // Arrange
        IAccountProvisioner provisioner = Substitute.For<IAccountProvisioner>();
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        ICredentialStore session = Substitute.For<ICredentialStore>();
        AccountSetup setup = new AccountSetup(provisioner, secure, session);

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(993), CancellationToken.None);

        // Assert
        Assert.That(result.Incoming, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(result.Outgoing, Is.EqualTo(Failure.PolicyBlocked));
        await secure.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        await session.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Test]
    public async Task AddAsync_WhenSecureStorageFails_RequiresExplicitSessionChoice()
    {
        // Arrange
        IAccountProvisioner provisioner = Substitute.For<IAccountProvisioner>();
        provisioner.NetworkAllowed.Returns(true);
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        secure.WriteAsync(Arg.Any<string>(), Arg.Any<Credential>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<bool>.Fail(Failure.Unavailable));
        ICredentialStore session = Substitute.For<ICredentialStore>();
        AccountSetup setup = new AccountSetup(provisioner, secure, session);

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(993), CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.CredentialStorageUnavailable));
        await session.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        await provisioner.DidNotReceiveWithAnyArgs().ConnectAccountAsync(default, default!, default!, default!,
            default!, default, default);
    }

    [Test]
    public async Task AddAsync_WhenOutgoingCertificateIsUntrusted_SeparatesIncomingAndOutgoingDiagnostics()
    {
        // Arrange
        await using LocalMailServer outgoing = new LocalMailServer(false);
        IAccountProvisioner provisioner = Substitute.For<IAccountProvisioner>();
        provisioner.NetworkAllowed.Returns(true);
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
        provisioner.Catalog.Returns(catalog);
        provisioner.ConnectAccountAsync(Arg.Any<AccountId>(), Arg.Any<string>(), Arg.Any<MailEndpoint>(),
            Arg.Any<MailEndpoint>(), Arg.Any<ICredentialStore>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<IMailAccountEngine>.Fail(Failure.NeedsAuthentication));
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        SessionCredentialStore session = new SessionCredentialStore();
        AccountSetup setup = new AccountSetup(provisioner, secure, session);

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(outgoing.Port) with
        {
            SessionOnly = true
        }, CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.None));
        Assert.That(result.Outgoing, Is.EqualTo(Failure.TlsRejected));
        Assert.That(result.Incoming, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(outgoing.Commands.Any(command => command.Contains("AUTH", StringComparison.Ordinal)), Is.False);
        await secure.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    private static AccountSetupRequest Request(int outgoingPort)
    {
        return new AccountSetupRequest("Synthetic", new MailEndpoint("localhost", 993, TlsMode.Implicit,
            "incoming-synthetic", ""), new MailEndpoint("localhost", outgoingPort, TlsMode.Implicit,
            "outgoing-synthetic", ""), "incoming-secret", "outgoing-secret", false, null);
    }
}

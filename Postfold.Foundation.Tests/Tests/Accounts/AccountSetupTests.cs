using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Desktop.Services;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class AccountSetupTests
{
    [Test]
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task AddAsync_WhenLocalTlsEndpointsHaveIndependentTrust_ReturnsIndependentResultsAndPersistsOnlyValidIncoming(
        bool incomingTrusted,
        bool outgoingTrusted
    )
    {
        // Arrange
        await using LocalMailServer incomingServer = new LocalMailServer(true);
        await using LocalMailServer outgoingServer = new LocalMailServer(false);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        IAccountProvisioner provisioner = Substitute.For<IAccountProvisioner>();
        provisioner.NetworkAllowed.Returns(true);
        provisioner.Catalog.Returns(profile);
        provisioner.TestOutgoingConnectionAsync(
            Arg.Any<AccountId>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(),
            Arg.Any<CancellationToken>()
        ).Returns(call =>
            {
                SmtpSubmitter submitter = new SmtpSubmitter(
                    call.Arg<AccountId>(),
                    call.Arg<MailEndpoint>(),
                    call.Arg<ICredentialStore>()
                );
                if (outgoingTrusted)
                {
                    submitter.FixtureAuthority = outgoingServer.Authority;
                }

                return submitter.TestAsync(call.Arg<CancellationToken>());
            });
        provisioner.ConnectAccountAsync(
            Arg.Any<AccountId>(),
            Arg.Any<string>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()
        )
            .Returns(async call =>
            {
                AccountId account = call.Arg<AccountId>();
                MailEndpoint incoming = call.ArgAt<MailEndpoint>(2);
                MailEndpoint outgoing = call.ArgAt<MailEndpoint>(3);
                CancellationToken cancellationToken = call.Arg<CancellationToken>();
                await using ImapAdapter adapter = new ImapAdapter(account, incoming, call.Arg<ICredentialStore>(), 1048576);
                if (incomingTrusted)
                {
                    adapter.FixtureAuthority = incomingServer.Authority;
                }

                Outcome<ImmutableArray<MailboxSnapshot>> discovered = await adapter.DiscoverAsync(cancellationToken);
                if (!discovered.IsSuccess)
                {
                    return Outcome<IMailAccountEngine>.Fail(discovered.Failure);
                }

                await profile.AddAccountAsync(account, call.ArgAt<string>(1), cancellationToken);
                await profile.ConfigureAccountAsync(account, incoming, outgoing, cancellationToken);
                IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
                engine.Mailboxes.Returns(discovered.Value);
                return Outcome<IMailAccountEngine>.Success(engine);
            });
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        SessionCredentialStore session = new SessionCredentialStore();
        AccountSetup setup = new AccountSetup(provisioner, secure, session);
        AccountSetupRequest request = new AccountSetupRequest(
            "Authored setup",
            new MailEndpoint("localhost", incomingServer.Port, TlsMode.Implicit, "incoming-synthetic", ""),
            new MailEndpoint("localhost", outgoingServer.Port, TlsMode.Implicit, "outgoing-synthetic", ""),
            "incoming-secret",
            "outgoing-secret",
            true,
            null
        );

        // Act
        AccountSetupResult result = await setup.AddAsync(request, CancellationToken.None);
        ImmutableArray<AccountConfiguration> accounts = await profile.AccountsAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.None));
        Assert.That(result.Incoming, Is.EqualTo(incomingTrusted ? Failure.None : Failure.TlsRejected));
        Assert.That(result.Outgoing, Is.EqualTo(outgoingTrusted ? Failure.None : Failure.TlsRejected));
        Assert.That(accounts.Length, Is.EqualTo(incomingTrusted ? 1 : 0));
        if (incomingTrusted)
        {
            Assert.That(result.Mailboxes, Is.Not.Empty);
            Assert.That(accounts[0].Incoming.Port, Is.EqualTo(incomingServer.Port));
            Assert.That(accounts[0].Outgoing.Port, Is.EqualTo(outgoingServer.Port));
            Assert.That(
                (await session.ReadAsync(accounts[0].Incoming.CredentialKey, CancellationToken.None)).Value!.Secret,
                Is.EqualTo("incoming-secret")
            );
            Assert.That(
                (await session.ReadAsync(accounts[0].Outgoing.CredentialKey, CancellationToken.None)).Value!.Secret,
                Is.EqualTo("outgoing-secret")
            );
        }

        Assert.That(incomingServer.Commands.Any(command => command.Contains("LOGIN", StringComparison.OrdinalIgnoreCase) ||
            command.Contains("AUTHENTICATE", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(incomingTrusted));
        Assert.That(
            outgoingServer.Commands.Any(command => command.Contains("AUTH", StringComparison.OrdinalIgnoreCase)),
            Is.EqualTo(outgoingTrusted)
        );
        Assert.That(outgoingServer.SubmissionCount, Is.Zero);
        await secure.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Test]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task RestoreCredentialsAsync_WhenStorageChoiceIsExplicit_UsesOnlyChosenStore(
        bool sessionOnly,
        bool storageFails
    )
    {
        // Arrange
        AccountId account = new AccountId(7);
        IAccountProvisioner host = Substitute.For<IAccountProvisioner>();
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        host.Catalog.Returns(catalog);
        MailEndpoint incoming = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "incoming", "incoming-key");
        MailEndpoint outgoing = new MailEndpoint("synthetic.invalid", 465, TlsMode.Implicit, "outgoing", "outgoing-key");
        catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(account, "Preserved account", incoming, outgoing)
        ));
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        ICredentialStore session = Substitute.For<ICredentialStore>();
        ICredentialStore chosen = sessionOnly ? session : secure;
        chosen.WriteAsync(Arg.Any<string>(), Arg.Any<Credential>(), Arg.Any<CancellationToken>()).Returns(
            storageFails ? Outcome<bool>.Fail(Failure.Unavailable) : Outcome<bool>.Success(true)
        );
        AccountSetup setup = new AccountSetup(host, secure, session);

        // Act
        Failure result = await setup.RestoreCredentialsAsync(
            account,
            "incoming-secret",
            "outgoing-secret",
            sessionOnly,
            CancellationToken.None
        );

        // Assert
        Assert.That(result, Is.EqualTo(storageFails ? Failure.CredentialStorageUnavailable : Failure.None));
        await chosen.Received(1).WriteAsync(
            "incoming-key",
            Arg.Is<Credential>(value => value.Secret == "incoming-secret"),
            Arg.Any<CancellationToken>()
        );
        await chosen.Received(1).WriteAsync(
            "outgoing-key",
            Arg.Is<Credential>(value => value.Secret == "outgoing-secret"),
            Arg.Any<CancellationToken>()
        );
        await (sessionOnly ? secure : session).DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        await host.Received(storageFails ? 0 : 1).ReplaceCredentialsAsync(account, chosen, Arg.Any<CancellationToken>());
        await host.DidNotReceiveWithAnyArgs().ConnectAccountAsync(
            default,
            default!,
            default!,
            default!,
            default!,
            default,
            default
        );
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
        await provisioner.DidNotReceiveWithAnyArgs().ConnectAccountAsync(
            default,
            default!,
            default!,
            default!,
            default!,
            default,
            default
        );
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
        provisioner.TestOutgoingConnectionAsync(
            Arg.Any<AccountId>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(),
            Arg.Any<CancellationToken>()
        ).Returns(call =>
                new SmtpSubmitter(call.Arg<AccountId>(), call.Arg<MailEndpoint>(), call.Arg<ICredentialStore>())
                    .TestAsync(call.Arg<CancellationToken>()));
        provisioner.ConnectAccountAsync(
            Arg.Any<AccountId>(),
            Arg.Any<string>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()
        )
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
        return new AccountSetupRequest("Synthetic", new MailEndpoint(
            "localhost",
            993,
            TlsMode.Implicit,
            "incoming-synthetic",
            ""
        ), new MailEndpoint(
    "localhost",
    outgoingPort,
    TlsMode.Implicit,
    "outgoing-synthetic",
    ""
), "incoming-secret", "outgoing-secret", false, null);
    }
}

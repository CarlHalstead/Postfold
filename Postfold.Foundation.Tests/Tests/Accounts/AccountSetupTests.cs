using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Desktop.Services;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services;
using Postfold.Sync.Services.Work;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class AccountSetupTests
{
    [Test]
    public async Task AddAsync_WhenSameAddressIsSubmittedConcurrently_AddsOnlyOneAccount()
    {
        // Arrange
        IAccountProvisioner host = Substitute.For<IAccountProvisioner>();
        host.NetworkAllowed.Returns(true);
        host.NextAccountIdAsync(Arg.Any<CancellationToken>()).Returns(new AccountId(1));
        ImmutableArray<AccountConfiguration> stored = [];
        host.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(_ => stored);
        TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray<MailboxSnapshot>.Empty);
        host.ConnectAccountAsync(Arg.Any<AccountId>(), Arg.Any<string>(), Arg.Any<MailEndpoint>(), Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult(true);
            await release.Task;
            stored = ImmutableArray.Create(new AccountConfiguration(call.Arg<AccountId>(), call.ArgAt<string>(1),
                call.ArgAt<MailEndpoint>(2), call.ArgAt<MailEndpoint>(3)));
            return Outcome<IMailAccountEngine>.Success(engine);
        });
        AccountSetup setup = new AccountSetup(host, new InMemoryCredentialStore());
        AccountSetupRequest request = Request(465);

        // Act
        Task<AccountSetupResult> first = setup.AddAsync(request, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<AccountSetupResult> second = setup.AddAsync(request, CancellationToken.None);
        bool secondWaited = !second.IsCompleted;
        release.TrySetResult(true);
        AccountSetupResult[] results = await Task.WhenAll(first, second);

        // Assert
        Assert.That(secondWaited, Is.True);
        Assert.That(results[0].Duplicate, Is.False);
        Assert.That(results[1].Duplicate, Is.True);
        Assert.That(stored, Has.Length.EqualTo(1));
    }

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
        provisioner.NextAccountIdAsync(Arg.Any<CancellationToken>()).Returns(new AccountId(1));
        provisioner.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
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
                engine.ReconcileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async sync =>
                {
                    await using ImapAdapter reader = new ImapAdapter(account, incoming, call.Arg<ICredentialStore>(), 1048576)
                    {
                        FixtureAuthority = incomingServer.Authority
                    };
                    MailboxSnapshot mailbox = discovered.Value.Single(item => item.RemoteKey == sync.Arg<string>());
                    return await new ReconciliationWork(reader, profile, mailbox, 100).RunAsync(sync.Arg<CancellationToken>());
                });
                return Outcome<IMailAccountEngine>.Success(engine);
            });
        InMemoryCredentialStore secure = new InMemoryCredentialStore();
        AccountSetup setup = new AccountSetup(provisioner, secure, new ClientIdentity(profile));
        AccountSetupRequest request = new AccountSetupRequest(
            "Authored setup",
            new MailEndpoint("localhost", incomingServer.Port, TlsMode.Implicit, "incoming-synthetic", ""),
            new MailEndpoint("localhost", outgoingServer.Port, TlsMode.Implicit, "outgoing-synthetic", ""),
            "incoming-secret",
            "outgoing-secret",
            null,
            "sender@synthetic.invalid"
        );

        // Act
        AccountSetupResult result = await setup.AddAsync(request, CancellationToken.None);
        ImmutableArray<AccountConfiguration> accounts = await profile.AccountsAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.None));
        Assert.That(result.Incoming, Is.EqualTo(incomingTrusted ? Failure.None : Failure.TlsRejected));
        Assert.That(result.Outgoing, Is.EqualTo(outgoingTrusted ? Failure.None : Failure.TlsRejected));
        Assert.That(accounts.Length, Is.EqualTo(incomingTrusted ? 1 : 0));
        Assert.That(result.Synchronisation, Is.EqualTo(Failure.None));
        Assert.That(await profile.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(incomingTrusted ? 2 : 0));
        if (incomingTrusted)
        {
            Assert.That(result.Mailboxes, Is.Not.Empty);
            Assert.That(accounts[0].Incoming.Port, Is.EqualTo(incomingServer.Port));
            Assert.That(accounts[0].Outgoing.Port, Is.EqualTo(outgoingServer.Port));
            Assert.That(accounts[0].Identity!.Sender, Is.EqualTo("sender@synthetic.invalid"));
            Assert.That(
                (await secure.ReadAsync(accounts[0].Incoming.CredentialKey, CancellationToken.None)).Value!.Secret,
                Is.EqualTo("incoming-secret")
            );
            Assert.That(
                (await secure.ReadAsync(accounts[0].Outgoing.CredentialKey, CancellationToken.None)).Value!.Secret,
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
    }

    [Test]
    [TestCase("sender@synthetic.invalid", "SENDER@synthetic.invalid", "other-login")]
    [TestCase(null, "LOGIN@synthetic.invalid", "login@synthetic.invalid")]
    public async Task AddAsync_WhenAddressIsAlreadyAdded_RejectsBeforeCredentialsOrNetwork(
        string? existingSender,
        string requestedSender,
        string existingLogin
    )
    {
        // Arrange
        IAccountProvisioner host = Substitute.For<IAccountProvisioner>();
        host.NetworkAllowed.Returns(true);
        AccountSetupRequest request = Request(465) with
        {
            Sender = requestedSender
        };
        host.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            new AccountConfiguration(new AccountId(1), "Different display name",
                request.Incoming with
                {
                    Username = existingLogin
                }, request.Outgoing,
                Identity: existingSender is null ? null : new Postfold.Core.Models.Drafts.ComposeIdentity(existingSender))
        ));
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        AccountSetup setup = new AccountSetup(host, secure, Substitute.For<IClientIdentity>());

        // Act
        AccountSetupResult result = await setup.AddAsync(request, CancellationToken.None);

        // Assert
        Assert.That(result.Duplicate, Is.True);
        await secure.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        await host.DidNotReceiveWithAnyArgs().TestOutgoingConnectionAsync(default, default!, default!, default);
        await host.DidNotReceiveWithAnyArgs().ConnectAccountAsync(default, default!, default!, default!, default!, default, default);
    }

    [Test]
    public async Task AddAsync_WhenInitialSyncFails_KeepsAddedAccountAndSeparatesSyncDiagnostic()
    {
        // Arrange
        IAccountProvisioner host = Substitute.For<IAccountProvisioner>();
        host.NetworkAllowed.Returns(true);
        host.NextAccountIdAsync(Arg.Any<CancellationToken>()).Returns(new AccountId(1));
        host.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
        IMailAccountEngine engine = Substitute.For<IMailAccountEngine>();
        engine.Mailboxes.Returns(ImmutableArray.Create(EngineFixture.Mailbox()));
        engine.ReconcileAsync("INBOX", Arg.Any<CancellationToken>()).Returns(Failure.Transient);
        host.ConnectAccountAsync(Arg.Any<AccountId>(), Arg.Any<string>(), Arg.Any<MailEndpoint>(), Arg.Any<MailEndpoint>(),
            Arg.Any<ICredentialStore>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<IMailAccountEngine>.Success(engine));
        AccountSetup setup = new AccountSetup(host, new InMemoryCredentialStore());

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(465), CancellationToken.None);

        // Assert
        Assert.That(result.Incoming, Is.EqualTo(Failure.None));
        Assert.That(result.Outgoing, Is.EqualTo(Failure.None));
        Assert.That(result.Credentials, Is.EqualTo(Failure.None));
        Assert.That(result.Synchronisation, Is.EqualTo(Failure.Transient));
        Assert.That(result.Mailboxes, Has.Length.EqualTo(1));
    }

    [Test]
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public async Task RestoreCredentialsAsync_WhenSecureWritesComplete_ReplacesCredentialsOnlyIfBothWereStored(
        bool incomingStored,
        bool outgoingStored
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
        secure.WriteAsync("incoming-key", Arg.Any<Credential>(), Arg.Any<CancellationToken>()).Returns(
            incomingStored ? Outcome<bool>.Success(true) : Outcome<bool>.Fail(Failure.Unavailable)
        );
        secure.WriteAsync("outgoing-key", Arg.Any<Credential>(), Arg.Any<CancellationToken>()).Returns(
            outgoingStored ? Outcome<bool>.Success(true) : Outcome<bool>.Fail(Failure.Unavailable)
        );
        AccountSetup setup = new AccountSetup(host, secure);

        // Act
        Failure result = await setup.RestoreCredentialsAsync(
            account,
            "incoming-secret",
            "outgoing-secret",
            CancellationToken.None
        );

        // Assert
        bool stored = incomingStored && outgoingStored;
        Assert.That(result, Is.EqualTo(stored ? Failure.None : Failure.CredentialStorageUnavailable));
        await secure.Received(1).WriteAsync(
            "incoming-key",
            Arg.Is<Credential>(value => value.Secret == "incoming-secret"),
            Arg.Any<CancellationToken>()
        );
        await secure.Received(1).WriteAsync(
            "outgoing-key",
            Arg.Is<Credential>(value => value.Secret == "outgoing-secret"),
            Arg.Any<CancellationToken>()
        );
        await host.Received(stored ? 1 : 0).ReplaceCredentialsAsync(account, secure, Arg.Any<CancellationToken>());
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
        AccountSetup setup = new AccountSetup(provisioner, secure);

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(993), CancellationToken.None);

        // Assert
        Assert.That(result.Incoming, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(result.Outgoing, Is.EqualTo(Failure.PolicyBlocked));
        await secure.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
    }

    [Test]
    public async Task AddAsync_WhenSecureStorageFails_RefusesAccountCreationWithoutContactingServers()
    {
        // Arrange
        IAccountProvisioner provisioner = Substitute.For<IAccountProvisioner>();
        provisioner.NetworkAllowed.Returns(true);
        provisioner.NextAccountIdAsync(Arg.Any<CancellationToken>()).Returns(new AccountId(1));
        provisioner.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
        ICredentialStore secure = Substitute.For<ICredentialStore>();
        secure.WriteAsync(Arg.Any<string>(), Arg.Any<Credential>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<bool>.Fail(Failure.Unavailable));
        AccountSetup setup = new AccountSetup(provisioner, secure);

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(993), CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.CredentialStorageUnavailable));
        await provisioner.DidNotReceiveWithAnyArgs().TestOutgoingConnectionAsync(default, default!, default!, default);
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
        provisioner.NextAccountIdAsync(Arg.Any<CancellationToken>()).Returns(new AccountId(1));
        provisioner.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<AccountConfiguration>.Empty);
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
        AccountSetup setup = new AccountSetup(provisioner, new InMemoryCredentialStore());

        // Act
        AccountSetupResult result = await setup.AddAsync(Request(outgoing.Port), CancellationToken.None);

        // Assert
        Assert.That(result.Credentials, Is.EqualTo(Failure.None));
        Assert.That(result.Outgoing, Is.EqualTo(Failure.TlsRejected));
        Assert.That(result.Incoming, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(outgoing.Commands.Any(command => command.Contains("AUTH", StringComparison.Ordinal)), Is.False);
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
), "incoming-secret", "outgoing-secret", null);
    }
}

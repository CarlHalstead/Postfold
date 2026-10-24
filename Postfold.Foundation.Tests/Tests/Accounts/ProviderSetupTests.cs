using Microsoft.Data.Sqlite;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Models.Authentication;
using Postfold.Desktop.Services;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Accounts;

public sealed class ProviderSetupTests
{
    [Test]
    public async Task ConnectProviderAsync_WhenBrowserSelectsGmail_DefersInitialSyncAndRejectsDuplicateWithoutChangingExistingGrant()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider);

        // Act
        ProviderConnectionResult connected = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", Synchronise: false), CancellationToken.None);
        long beforeSync = await runtime.Catalog.CachedMessageCountAsync(CancellationToken.None);
        Failure synced = await runtime.Application.SynchroniseAsync(connected.Account!.Value, CancellationToken.None);
        byte[] grant = provider.Authentication.Stored.Single().Value.ToArray();
        ProviderConnectionResult duplicate = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", Synchronise: false), CancellationToken.None);

        // Assert
        Assert.That(connected.Failure, Is.EqualTo(Failure.None));
        Assert.That(beforeSync, Is.Zero);
        Assert.That(synced, Is.EqualTo(Failure.None));
        Assert.That(await runtime.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
        Assert.That((await runtime.Catalog.AccountsAsync(CancellationToken.None)).Single().Address, Is.EqualTo(GmailFixture.Binding.Address));
        Assert.That(duplicate.Duplicate, Is.True);
        Assert.That(provider.Authentication.Stored.Single().Value, Is.EqualTo(grant));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public void Load_WhenGoogleDesktopFieldIsProvided_RetainsItWithoutFormattingItIntoDiagnostics()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "provider-clients.json");
        File.WriteAllText(path, "{\"GoogleClientId\":\"synthetic.apps.googleusercontent.com\",\"GooglePolicyDecisionReference\":\"synthetic decision\",\"GoogleClientSecret\":\"synthetic-desktop-client-field\"}");

        // Act
        ProviderClientConfiguration configuration = ProviderClientConfiguration.Load(path);

        // Assert
        Assert.That(configuration.Availability("Google"), Is.EqualTo(Failure.None));
        Assert.That(configuration.GoogleClientSecret, Is.EqualTo("synthetic-desktop-client-field"));
        Assert.That(configuration.ToString(), Does.Not.Contain("synthetic-desktop-client-field"));
    }

    [Test]
    public async Task ConnectProviderAsync_WhenConcurrentDuplicateIsAdded_RetainsOneRealBindingWithoutEndpointsOrPasswordWrites()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider, passwords);
        AccountSetup setup = new AccountSetup(runtime, passwords, runtime.Identity);
        ProviderSetupRequest request = new ProviderSetupRequest("Google", GmailFixture.Binding.Address);

        // Act
        ProviderConnectionResult[] results = await Task.WhenAll(
            setup.ConnectProviderAsync(request, CancellationToken.None),
            setup.ConnectProviderAsync(request, CancellationToken.None)
        );
        AccountConfiguration saved = (await runtime.Catalog.AccountsAsync(CancellationToken.None)).Single();

        // Assert
        Assert.That(results.Count(item => item.Failure == Failure.None), Is.EqualTo(1));
        Assert.That(results.Count(item => item.Duplicate), Is.EqualTo(1));
        Assert.That(saved.GoogleBinding, Is.EqualTo(GmailFixture.Binding));
        Assert.That(saved.Incoming, Is.Null);
        Assert.That(saved.Outgoing, Is.Null);
        Assert.That(saved.Address, Is.EqualTo(GmailFixture.Binding.Address));
        Assert.That(await runtime.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
        Assert.That(provider.Authentication.TokenRequests, Has.Count.EqualTo(1));
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.DraftWrites, Is.Zero);
        Assert.That(passwords.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task ConnectProviderAsync_WhenDesktopRegistrationRequiresClientSecret_ReopensAndRefreshesWithoutBrowser()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        provider.Authentication.RequiredGoogleClientSecret = "synthetic-desktop-client-field";
        provider.Authentication.TokenJson = provider.Authentication.TokenJson.Replace("3600", "30", StringComparison.Ordinal);
        ProviderConnectionResult connected;
        await using (DesktopMailRuntime runtime = await GoogleRuntimeAsync(directory, provider, clientSecret: provider.Authentication.RequiredGoogleClientSecret))
        {
            // Act
            connected = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None);
        }
        Uri? browser = provider.Authentication.Authorization;
        int exchanges = provider.Authentication.TokenRequests.Count;
        await using DesktopMailRuntime reopened = await GoogleRuntimeAsync(directory, provider, clientSecret: provider.Authentication.RequiredGoogleClientSecret);
        Failure reconciled = await reopened.Find(connected.Account!.Value)!.ReconcileAsync("INBOX", CancellationToken.None);

        // Assert
        Assert.That(connected.Failure, Is.EqualTo(Failure.None));
        Assert.That(reconciled, Is.EqualTo(Failure.None));
        Assert.That(provider.Authentication.Authorization, Is.SameAs(browser));
        Assert.That(provider.Authentication.TokenRequests.Count, Is.GreaterThan(exchanges));
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.DraftWrites, Is.Zero);
    }

    [TestCase(false, false, Failure.Unavailable)]
    [TestCase(true, false, Failure.PolicyBlocked)]
    public async Task ConnectProviderAsync_WhenRegistrationOrPolicyIsMissing_DoesNotOpenBrowser(
        bool registered,
        bool policy,
        Failure expected
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await OpenAsync(EngineFixture.DirectoryPath());
        runtime.ConfigureProviderClients(new ProviderClientConfiguration(
            registered ? GmailFixture.Binding.ClientId : null,
            GooglePolicyDecisionReference: policy ? "synthetic policy fixture" : null
        ));
        await runtime.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), provider.Authentication, provider.Authentication, CancellationToken.None);

        // Act
        ProviderConnectionResult result = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(expected));
        Assert.That(result.Account, Is.Null);
        Assert.That(await runtime.Catalog.AccountsAsync(CancellationToken.None), Is.Empty);
        Assert.That(provider.Authentication.Authorization, Is.Null);
        Assert.That(provider.Requests, Is.Empty);
    }

    [TestCase("store", Failure.CredentialStorageUnavailable, false)]
    [TestCase("corrupt", Failure.Corrupt, false)]
    [TestCase("write", Failure.CredentialStorageUnavailable, false)]
    [TestCase("browser", Failure.Unavailable, false)]
    [TestCase("identity", Failure.NeedsReview, false)]
    [TestCase("discovery", Failure.PolicyBlocked, true)]
    [TestCase("page", Failure.PolicyBlocked, true)]
    public async Task ConnectProviderAsync_WhenBoundaryFails_ReportsFailureAndOnlyRetainsVerifiedRecoverableAccount(
        string boundary,
        Failure expected,
        bool retained
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider);
        provider.Authentication.StoreFailure = boundary == "store" ? Failure.CredentialStorageUnavailable :
            boundary == "corrupt" ? Failure.Corrupt : Failure.None;
        provider.Authentication.WriteFailure = boundary == "write" ? Failure.CredentialStorageUnavailable : Failure.None;
        provider.Authentication.BrowserFailure = boundary == "browser" ? Failure.Unavailable : Failure.None;
        if (boundary == "identity")
        {
            provider.Authentication.Subject = "other@example.test";
        }
        provider.Intercept = (request, _) => Task.FromResult(
            boundary == "discovery" && request.RequestUri!.AbsolutePath.EndsWith("/labels", StringComparison.Ordinal) ||
            boundary == "page" && request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) ?
                GmailFixture.Reply("{}", HttpStatusCode.Forbidden) : null
        );

        // Act
        ProviderConnectionResult result = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None);

        // Assert
        Assert.That(boundary == "page" ? result.Synchronisation : result.Failure, Is.EqualTo(expected));
        Assert.That(result.Account is not null, Is.EqualTo(retained));
        Assert.That(await runtime.Catalog.AccountsAsync(CancellationToken.None), Has.Length.EqualTo(retained ? 1 : 0));
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.DraftWrites, Is.Zero);
        provider.Intercept = null;
        if (retained)
        {
            if (boundary == "discovery")
            {
                Assert.That(await runtime.Application.SynchroniseAsync(result.Account!.Value, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
            }
            ProviderConnectionResult recovered = await runtime.ReconnectProviderAsync(result.Account!.Value, CancellationToken.None);
            Assert.That(recovered.Failure, Is.EqualTo(Failure.None));
            Assert.That(recovered.Synchronisation, Is.EqualTo(Failure.None));
            Assert.That(await runtime.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReconnectProviderAsync_WhenTokensDeletedAndProfileReopened_PreservesHeldDraftAndFrozenUncertainSubmission(
        bool wrongIdentity
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        FrozenSubmission frozen;
        Guid draftId = Guid.NewGuid();
        DraftSnapshot held;
        CommandSnapshot pending;
        await using (DesktopMailRuntime runtime = await GoogleRuntimeAsync(directory, provider))
        {
            Assert.That(
                (await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None)).Failure,
                Is.EqualTo(Failure.None)
            );
            IMailAccountEngine engine = runtime.Find(new AccountId(1))!;
            ComposeContent local = new ComposeContent(
                GmailFixture.Binding.Address,
                ["first@synthetic.invalid"],
                "synthetic draft",
                "original local work",
                DateTimeOffset.UtcNow,
                "draft@synthetic.invalid"
            );
            await runtime.Application.SaveDraftAsync(engine.Account, draftId, local, CancellationToken.None);
            Assert.That(await engine.PushDraftAsync(draftId, CancellationToken.None), Is.EqualTo(Failure.None));
            await runtime.Application.SaveDraftAsync(engine.Account, draftId, local with { PlainText = "new local work" }, CancellationToken.None);
            provider.ChangeDraftVersion("draft-1");
            Assert.That(await engine.PushDraftAsync(draftId, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
            held = (await runtime.Catalog.DraftsAsync(engine.Account, CancellationToken.None)).Single();
            ComposeContent content = new ComposeContent(
                GmailFixture.Binding.Address,
                ["first@synthetic.invalid"],
                "synthetic frozen",
                "preserved body",
                DateTimeOffset.UtcNow,
                "synthetic@invalid"
            );
            frozen = (await engine.QueueSubmissionAsync(content, null, null, CancellationToken.None)).Value!;
            provider.LoseSendAcknowledgement = true;
            await engine.SubmitAsync(frozen.Id, CancellationToken.None);
            Assert.That(await runtime.DeleteProviderTokensAsync(engine.Account, CancellationToken.None), Is.EqualTo(Failure.None));
            MessageSnapshot message = (await runtime.Query.SearchAsync(new SearchRequest(engine.Account, false, "", null, false, 10), CancellationToken.None)).Value!.Messages[0];
            Assert.That(await runtime.Application.MutateAsync(message, MutationKind.SetFlagged, true, null, CancellationToken.None), Is.EqualTo(Failure.NeedsAuthentication));
            pending = (await runtime.Catalog.CommandsAsync(engine.Account, CancellationToken.None)).Single();
        }
        await using DesktopMailRuntime reopened = await GoogleRuntimeAsync(directory, provider);
        Uri? priorBrowser = provider.Authentication.Authorization;
        Assert.That(await reopened.Find(frozen.Account)!.ReconcileAsync("INBOX", CancellationToken.None), Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(provider.Authentication.Authorization, Is.SameAs(priorBrowser));
        provider.Authentication.Subject = wrongIdentity ? "wrong@example.test" : GmailFixture.Binding.Address;

        // Act
        ProviderConnectionResult result = await reopened.ReconnectProviderAsync(frozen.Account, CancellationToken.None);
        await reopened.Application.ProcessOutboxAsync(frozen.Account, CancellationToken.None);
        await reopened.Application.PushDraftAsync(frozen.Account, draftId, CancellationToken.None);
        OutboxEntry saved = (await reopened.Catalog.OutboxAsync(frozen.Account, CancellationToken.None)).Single();
        DraftSnapshot preserved = (await reopened.Catalog.DraftsAsync(frozen.Account, CancellationToken.None)).Single();

        // Assert
        Assert.That(result.Failure, Is.EqualTo(wrongIdentity ? Failure.NeedsReview : Failure.None));
        Assert.That(saved.State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(saved.Submission.Mime, Is.EqualTo(frozen.Mime));
        Assert.That(saved.Submission.Sender, Is.EqualTo(frozen.Sender));
        Assert.That(saved.Submission.Recipients, Is.EqualTo(frozen.Recipients));
        Assert.That(saved.Evidence!.Recipients.All(item => item.State == RecipientState.Uncertain), Is.True);
        Assert.That(preserved.Content, Is.EqualTo(held.Content));
        Assert.That(preserved.Held, Is.True);
        Assert.That(preserved.Conflict, Is.True);
        Assert.That((await reopened.Catalog.CommandsAsync(frozen.Account, CancellationToken.None)).Single(), Is.EqualTo(pending));
        Assert.That(provider.Sends, Is.EqualTo(1));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(await reopened.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConnectProviderAsync_WhenBrowserWaitIsCancelledOrShutdown_DrainsCallbackAndRetainsNoAccount(
        bool shutdown
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        provider.Authentication.SendCallback = false;
        DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider);
        using CancellationTokenSource stop = new CancellationTokenSource();
        bool disposed = false;
        Task<ProviderConnectionResult> connecting = runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), stop.Token);
        try
        {
            await provider.Authentication.Opened.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            if (shutdown)
            {
                await runtime.DisposeAsync();
                disposed = true;
            }
            else
            {
                await stop.CancelAsync();
            }
            ProviderConnectionResult result = await connecting.WaitAsync(TimeSpan.FromSeconds(10));

            // Assert
            Assert.That(result.Cancelled, Is.True);
            Assert.That(result.Account, Is.Null);
            Assert.That(provider.Authentication.TokenRequests, Is.Empty);
            if (!shutdown)
            {
                Assert.That(await runtime.Catalog.AccountsAsync(CancellationToken.None), Is.Empty);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await connecting.WaitAsync(TimeSpan.FromSeconds(10));
            if (!disposed)
            {
                await runtime.DisposeAsync();
            }
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task ConnectProviderAsync_WhenMicrosoftMailboxIsSelected_VerifiesBothTlsTransportsWithoutInferringAddressFromHomeIdentity(
        bool rejected,
        bool automatic
    )
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture

        {
            ApplicationId = "66666666-6666-6666-6666-666666666666"
        };
        await using LocalMailServer imap = new LocalMailServer(true)

        {
            AdvertiseOAuth = true,
            ExpectedBearer = provider.AccessSecret,
            AuthenticationFailure = rejected ? "[AUTHENTICATIONFAILED] synthetic mailbox denied" : null
        };
        await using LocalMailServer smtp = new LocalMailServer(false)

        {
            AdvertiseOAuth = true,
            ExpectedBearer = provider.AccessSecret
        };
        await using DesktopMailRuntime runtime = await OpenAsync(EngineFixture.DirectoryPath());
        runtime.ConfigureProviderClients(new ProviderClientConfiguration(MicrosoftClientId: provider.ApplicationId));
        runtime.AuthenticationHttpFactory = provider;
        runtime.ConfigureFixtureTransports = (adapter, submitter) =>
        {
            adapter.FixtureAuthority = imap.Authority;
            submitter.FixtureAuthority = smtp.Authority;
        };
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        await runtime.RestoreAccountsAsync(passwords, provider.Shared, provider.Shared, CancellationToken.None);
        string selectedAddress = automatic ? "" : "synthetic@example.test";
        ProviderSetupRequest request = new ProviderSetupRequest(
            "Microsoft",
            selectedAddress,
            new MailEndpoint("localhost", imap.Port, TlsMode.Implicit, selectedAddress, ""),
            new MailEndpoint("localhost", smtp.Port, TlsMode.Implicit, selectedAddress, "")
        );

        // Act
        ProviderConnectionResult result = await runtime.ConnectProviderAsync(request, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(rejected ? Failure.NeedsAuthentication : Failure.None));
        AccountConfiguration[] accounts = (await runtime.Catalog.AccountsAsync(CancellationToken.None)).ToArray();
        Assert.That(accounts, Has.Length.EqualTo(rejected ? 0 : 1));
        Assert.That(smtp.SubmissionCount, Is.Zero);
        Assert.That(passwords.ReceivedCalls(), Is.Empty);
        if (rejected)
        {
            Assert.That(provider.Shared.Stored, Is.Empty);
        }
        if (!rejected)
        {
            Assert.That(accounts[0].Address, Is.EqualTo("synthetic@example.test"));
            Assert.That((accounts[0] with { Identity = new ComposeIdentity("alias@example.test") }).Address, Is.EqualTo("synthetic@example.test"));
            Assert.That(
                accounts[0].MicrosoftBinding!.HomeAccountId,
                Is.EqualTo(MicrosoftAuthenticationFixture.User + "." + MicrosoftAuthenticationFixture.Tenant)
            );
            Assert.That(imap.BearerAuthentications, Is.Not.Empty);
            Assert.That(smtp.BearerAuthentications, Is.Not.Empty);
            Assert.That(await runtime.RevokeProviderAsync(accounts[0].Id, CancellationToken.None), Is.EqualTo(Failure.Unsupported));
            Assert.That(await runtime.DeleteProviderTokensAsync(accounts[0].Id, CancellationToken.None), Is.EqualTo(Failure.None));
            provider.HomeUser = "77777777-7777-7777-7777-777777777777";
            Assert.That((await runtime.ReconnectProviderAsync(accounts[0].Id, CancellationToken.None)).Failure, Is.EqualTo(Failure.NeedsReview));
            Assert.That((await runtime.Catalog.AccountsAsync(CancellationToken.None)).Single().MicrosoftBinding, Is.EqualTo(accounts[0].MicrosoftBinding));
        }
    }

    [TestCase("account", false)]
    [TestCase("binding", false)]
    [TestCase("identity", false)]
    [TestCase("mailboxes", true)]
    [TestCase("capabilities", true)]
    [TestCase("token-removal", false)]
    public async Task ConnectProviderAsync_WhenSqliteBoundaryFails_RollsBackProvisioningOrRetainsRecoverableBinding(
        string boundary,
        bool retained
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(directory, provider);
        provider.Authentication.DeleteFailure = boundary == "token-removal" ? Failure.CredentialStorageUnavailable : Failure.None;
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder

        {
            DataSource = Path.Combine(directory, "profile.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        string trigger = boundary switch
        {
            "account" => "BEFORE INSERT ON Accounts",
            "binding" or "token-removal" => "BEFORE INSERT ON Preferences WHEN NEW.Key='google-binding'",
            "identity" => "BEFORE INSERT ON Preferences WHEN NEW.Key='primary'",
            "mailboxes" => "BEFORE INSERT ON Collections",
            _ => "BEFORE UPDATE ON Accounts WHEN NEW.Capabilities<>OLD.Capabilities"
        };
        command.CommandText = "CREATE TRIGGER synthetic_fault " + trigger + " BEGIN SELECT RAISE(ABORT,'synthetic persistence fault'); END;";
        await command.ExecuteNonQueryAsync();

        // Act
        ProviderConnectionResult result = await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(boundary == "token-removal" ? Failure.CredentialStorageUnavailable : Failure.Unavailable));
        Assert.That(result.Account is not null, Is.EqualTo(retained));
        Assert.That(await runtime.Catalog.AccountsAsync(CancellationToken.None), Has.Length.EqualTo(retained ? 1 : 0));
        Assert.That(runtime.Find(new AccountId(1)) is not null, Is.EqualTo(retained));
        if (!retained)
        {
            Assert.That(provider.Authentication.Stored.Count, Is.EqualTo(boundary == "token-removal" ? 1 : 0));
        }
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.DraftWrites, Is.Zero);
    }

    [Test]
    [Platform("Win")]
    public async Task ConnectProviderAsync_WhenRealWindowsTokenStoreIsUsed_ReopensSilentlyAndDeletesOnlyProtectedTokens()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        WindowsTokenStore tokens = new WindowsTokenStore(Path.Combine(directory, "tokens"));
        await using (DesktopMailRuntime runtime = await OpenAsync(directory))
        {
            runtime.ConfigureProviderClients(new ProviderClientConfiguration(
                GmailFixture.Binding.ClientId,
                GooglePolicyDecisionReference: "synthetic-only policy decision"
            ));
            runtime.GoogleHttpFactory = provider.Http;
            await runtime.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), tokens, provider.Authentication, CancellationToken.None);

            // Act
            Assert.That(
                (await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None)).Failure,
                Is.EqualTo(Failure.None)
            );
        }
        await using DesktopMailRuntime reopened = await OpenAsync(directory);
        reopened.GoogleHttpFactory = provider.Http;
        await reopened.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), tokens, provider.Authentication, CancellationToken.None);
        Uri? browser = provider.Authentication.Authorization;
        Assert.That(await reopened.Find(new AccountId(1))!.ReconcileAsync("INBOX", CancellationToken.None), Is.EqualTo(Failure.None));
        Assert.That(await reopened.DeleteProviderTokensAsync(new AccountId(1), CancellationToken.None), Is.EqualTo(Failure.None));

        // Assert
        Assert.That(provider.Authentication.Authorization, Is.SameAs(browser));
        Assert.That((await tokens.ReadAsync("google:" + GmailFixture.Binding.ClientId + ":1", CancellationToken.None)).Failure, Is.EqualTo(Failure.NotFound));
        Assert.That(await reopened.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task ConnectProviderAsync_WhenInitialPageIsCancelled_RetainsAccountAndRecoversWithoutSubmittingMail()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Intercept = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
            }
            return null;
        };
        using CancellationTokenSource stop = new CancellationTokenSource();
        Task<ProviderConnectionResult> connecting = runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await stop.CancelAsync();
            ProviderConnectionResult cancelled = await connecting.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Intercept = null;
            ProviderConnectionResult recovered = await runtime.ReconnectProviderAsync(new AccountId(1), CancellationToken.None);

            // Assert
            Assert.That(cancelled.Account, Is.EqualTo(new AccountId(1)));
            Assert.That(cancelled.Cancelled || cancelled.Failure != Failure.None || cancelled.Synchronisation != Failure.None, Is.True);
            Assert.That(recovered.Failure, Is.EqualTo(Failure.None));
            Assert.That(recovered.Synchronisation, Is.EqualTo(Failure.None));
            Assert.That(await runtime.Catalog.AccountsAsync(CancellationToken.None), Has.Length.EqualTo(1));
            Assert.That(await runtime.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
            Assert.That(provider.Sends, Is.Zero);
            Assert.That(provider.DraftWrites, Is.Zero);
        }
        finally
        {
            await stop.CancelAsync();
            await connecting.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task ReconnectProviderAsync_WhenOwnerConfigurationChangesClient_RefusesSubstitutionBeforeBrowser()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await using (DesktopMailRuntime runtime = await GoogleRuntimeAsync(directory, provider))
        {
            await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google", GmailFixture.Binding.Address), CancellationToken.None);
        }
        await using DesktopMailRuntime reopened = await OpenAsync(directory);
        reopened.ConfigureProviderClients(new ProviderClientConfiguration(
            "changed.apps.googleusercontent.com",
            GooglePolicyDecisionReference: "synthetic-only policy decision"
        ));
        reopened.GoogleHttpFactory = provider.Http;
        await reopened.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), provider.Authentication, provider.Authentication, CancellationToken.None);
        Uri? browser = provider.Authentication.Authorization;

        // Act
        ProviderConnectionResult result = await reopened.ReconnectProviderAsync(new AccountId(1), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(provider.Authentication.Authorization, Is.SameAs(browser));
        Assert.That((await reopened.Catalog.AccountsAsync(CancellationToken.None)).Single().GoogleBinding, Is.EqualTo(GmailFixture.Binding));
        Assert.That(provider.Sends, Is.Zero);
    }

    [Test]
    public async Task ConnectProviderAsync_WhenDeactivatedAddressIsAddedAgain_AllocatesFreshAccountWithoutReusingRetainedIdentity()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GoogleRuntimeAsync(EngineFixture.DirectoryPath(), provider);
        ProviderSetupRequest request = new ProviderSetupRequest("Google", GmailFixture.Binding.Address);
        Assert.That((await runtime.ConnectProviderAsync(request, CancellationToken.None)).Account, Is.EqualTo(new AccountId(1)));
        Assert.That(await runtime.RemoveAsync(new AccountId(1), CancellationToken.None), Is.EqualTo(Failure.None));

        // Act
        ProviderConnectionResult added = await runtime.ConnectProviderAsync(request, CancellationToken.None);

        // Assert
        Assert.That(added.Failure, Is.EqualTo(Failure.None));
        Assert.That(added.Account, Is.EqualTo(new AccountId(2)));
        Assert.That(runtime.Find(new AccountId(1)), Is.Null);
        Assert.That((await runtime.Catalog.AccountsAsync(CancellationToken.None)).Single().Id, Is.EqualTo(new AccountId(2)));
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.DraftWrites, Is.Zero);
    }

    private static async Task<DesktopMailRuntime> GoogleRuntimeAsync(
        string directory,
        GmailFixture provider,
        ICredentialStore? passwords = null,
        string? clientSecret = null
    )
    {
        DesktopMailRuntime runtime = await OpenAsync(directory);
        runtime.ConfigureProviderClients(new ProviderClientConfiguration(
            GmailFixture.Binding.ClientId,
            GooglePolicyDecisionReference: "synthetic-only policy decision",
            GoogleClientSecret: clientSecret
        ));
        runtime.GoogleHttpFactory = provider.Http;
        await runtime.RestoreAccountsAsync(passwords ?? Substitute.For<ICredentialStore>(), provider.Authentication, provider.Authentication, CancellationToken.None);
        return runtime;
    }

    private static async Task<DesktopMailRuntime> OpenAsync(
        string directory
    )
    {
        return (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(true, false, false),
            TimeProvider.System,
            new EngineLimits(2, 32, 1, 10, 1048576, 50, 0, 0, 1048576),
            CancellationToken.None
        )).Value!;
    }
}

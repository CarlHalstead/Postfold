using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services;
using Postfold.Desktop.Services.Authentication;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Policies;
using Postfold.Sync.Services.Work;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class MicrosoftTransportTests
{
    private const string Mime = "From: sender@synthetic.invalid\r\nTo: first@synthetic.invalid\r\nSubject: frozen synthetic\r\n\r\nfrozen synthetic body\r\n";
    private static readonly MicrosoftAccountBinding Binding = new MicrosoftAccountBinding(
        "55555555-5555-5555-5555-555555555555",
        MicrosoftAuthenticationFixture.User + "." + MicrosoftAuthenticationFixture.Tenant,
        MicrosoftAuthenticationFixture.Tenant
    );

    private static MailboxSnapshot SentMailbox => new MailboxSnapshot(EngineFixture.Account, new FolderId(0), "Sent", "Sent", MailboxRole.Sent, true, true, 7);

    [Test]
    public async Task ResolveAsync_WhenCacheReopensAndRotates_AuthenticatesBothTransportsWithFreshBoundToken()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture
        {
            ExpiresIn = 30
        };
        await SeedAuthenticationAsync(provider);
        provider.AccessSecret = "synthetic-rotated-access";
        provider.RefreshSecret = "synthetic-rotated-refresh";
        provider.ExpiresIn = 3600;
        await using LocalMailServer imap = Server(true, provider.AccessSecret);
        await using LocalMailServer smtp = Server(false, provider.AccessSecret);
        AccountConfiguration account = Configuration(imap, smtp);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        await using ImapAdapter adapter = Adapter(account, credentials, imap);
        SmtpSubmitter submitter = Submitter(account, credentials, smtp);
        Uri? originalBrowser = provider.Shared.Authorization;

        // Act
        Outcome<Credential>[] resolved = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            credentials.ResolveAsync(account.Id, account.Incoming!, CancellationToken.None)));
        Outcome<ImmutableArray<MailboxSnapshot>> folders = await adapter.DiscoverAsync(CancellationToken.None);
        Failure outgoing = await submitter.TestAsync(CancellationToken.None);
        await using MicrosoftAccountAuthenticator reopened = Authenticator(provider);
        Outcome<AccessToken> restored = await reopened.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(resolved.All(result => result.IsSuccess && result.Value!.BearerToken && result.Value.Secret == provider.AccessSecret), Is.True);
        Assert.That(folders.IsSuccess, Is.True);
        Assert.That(outgoing, Is.EqualTo(Failure.None));
        Assert.That(restored.Value!.Secret, Is.EqualTo(provider.AccessSecret));
        Assert.That(restored.Value.Resource, Is.EqualTo("https://outlook.office.com"));
        Assert.That(provider.Exchanges, Is.EqualTo(2));
        Assert.That(provider.Shared.Authorization, Is.SameAs(originalBrowser));
        Assert.That(imap.BearerAuthentications, Is.EqualTo(smtp.BearerAuthentications));
        Assert.That(imap.BearerAuthentications.Single(), Does.Contain(provider.AccessSecret));
        Assert.That(imap.Commands.Concat(smtp.Commands).Any(command => command.Contains("PLAIN", StringComparison.Ordinal)), Is.False);
    }

    [TestCase(Failure.NeedsAuthentication)]
    [TestCase(Failure.CredentialStorageUnavailable)]
    [TestCase(Failure.Corrupt)]
    [TestCase(Failure.PolicyBlocked)]
    public async Task SubmitAsync_WhenAcquisitionFails_PreservesPreconnectEvidenceWithoutSubmission(Failure failure)
    {
        // Arrange
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration account = Configuration(imap, smtp);
        IAccountAuthenticator authenticator = Substitute.For<IAccountAuthenticator>();
        authenticator.AcquireAsync(false, Arg.Any<CancellationToken>()).Returns(Outcome<AccessToken>.Fail(failure));
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, Mime));
        await profile.QueueAsync(submission, CancellationToken.None);
        OutboxWork work = new OutboxWork(account.Id, profile, profile.Blobs, Submitter(account, credentials, smtp), submission.Id);

        // Act
        Failure result = await work.RunAsync(CancellationToken.None);
        await work.RunAsync(CancellationToken.None);
        OutboxEntry entry = (await profile.OutboxAsync(account.Id, CancellationToken.None)).Single();

        // Assert
        Assert.That(result, Is.EqualTo(failure));
        Assert.That(entry.State, Is.EqualTo(SubmissionState.Rejected));
        Assert.That(entry.Evidence!.Phase, Is.EqualTo(SubmissionPhase.BeforeConnect));
        Assert.That(entry.Evidence.Failure, Is.EqualTo(failure));
        Assert.That(entry.Evidence.Recipients.All(recipient => recipient.State == RecipientState.NotAccepted), Is.True);
        Assert.That(entry.Submission.Mime, Is.EqualTo(submission.Mime));
        Assert.That(smtp.SubmissionCount, Is.Zero);
        Assert.That(smtp.Connections, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_WhenAcknowledgementLostOrSentCopyAuthenticationFails_NeverSubmitsAgainAfterReopen(bool loseAcknowledgement)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        smtp.LoseAcknowledgement = loseAcknowledgement;
        AccountConfiguration account = Configuration(imap, smtp);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        FrozenSubmission submission = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, Mime));
        await profile.QueueAsync(submission, CancellationToken.None);
        SmtpSubmitter submitter = Submitter(account, credentials, smtp);

        // Act
        await new OutboxWork(account.Id, profile, profile.Blobs, submitter, submission.Id).RunAsync(CancellationToken.None);
        if (!loseAcknowledgement)
        {
            await authenticator.DeleteCredentialsAsync(CancellationToken.None);
            await using ImapAdapter sent = Adapter(account, credentials, imap);
            sent.RestoreMailboxes([SentMailbox]);
            Failure copied = await new SentCopyWork(submission.Id, account.Id, profile, profile.Blobs, sent).RunAsync(CancellationToken.None);
            Assert.That(copied, Is.EqualTo(Failure.NeedsAuthentication));
        }
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await reopened.RecoverAsync(CancellationToken.None);
        await new OutboxWork(account.Id, reopened, reopened.Blobs, submitter, submission.Id).RunAsync(CancellationToken.None);
        OutboxEntry entry = (await reopened.OutboxAsync(account.Id, CancellationToken.None)).Single();

        // Assert
        Assert.That(smtp.SubmissionCount, Is.EqualTo(1));
        Assert.That(entry.State, Is.EqualTo(loseAcknowledgement ? SubmissionState.Uncertain : SubmissionState.SentCopyPending));
        Assert.That(entry.Evidence!.Recipients.All(recipient => recipient.State ==
            (loseAcknowledgement ? RecipientState.Uncertain : RecipientState.Accepted)), Is.True);
        Assert.That(entry.Submission.Mime, Is.EqualTo(submission.Mime));
        Assert.That(smtp.SubmittedMime, Does.Contain("frozen synthetic body"));
        Assert.That(imap.Connections, Is.Zero);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AuthenticateAsync_WhenOAuthNotAdvertised_ReportsPolicyWithoutPasswordFallback(bool incoming)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        imap.AdvertiseOAuth = false;
        smtp.AdvertiseOAuth = false;
        AccountConfiguration account = Configuration(imap, smtp);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        await using ImapAdapter adapter = Adapter(account, credentials, imap);

        // Act
        Failure result = incoming ? (await adapter.DiscoverAsync(CancellationToken.None)).Failure :
            await Submitter(account, credentials, smtp).TestAsync(CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(imap.BearerAuthentications.Concat(smtp.BearerAuthentications), Is.Empty);
        Assert.That(imap.Commands.Concat(smtp.Commands).Any(command => command.Contains("AUTH ", StringComparison.Ordinal)), Is.False);
        Assert.That(smtp.SubmissionCount, Is.Zero);
    }

    [TestCase("account")]
    [TestCase("subject")]
    [TestCase("tenant")]
    [TestCase("resource")]
    [TestCase("expiry")]
    public async Task ResolveAsync_WhenTokenBindingIsWrong_RejectsWithoutReturningCredential(string mismatch)
    {
        // Arrange
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration account = Configuration(imap, smtp);
        IAccountAuthenticator authenticator = Substitute.For<IAccountAuthenticator>();
        AccessToken token = new AccessToken(
            mismatch == "account" ? new AccountId(2) : account.Id,
            mismatch == "subject" ? "other" : Binding.HomeAccountId,
            "synthetic-private-token",
            mismatch == "expiry" ? DateTimeOffset.UnixEpoch : DateTimeOffset.UtcNow.AddHours(1),
            mismatch == "tenant" ? "other" : Binding.TenantId,
            mismatch == "resource" ? "https://graph.microsoft.com" : "https://outlook.office.com"
        );
        authenticator.AcquireAsync(false, Arg.Any<CancellationToken>()).Returns(Outcome<AccessToken>.Success(token));
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);

        // Act
        Outcome<Credential> result = await credentials.ResolveAsync(account.Id, account.Incoming!, CancellationToken.None);
        MailEndpoint foreign = account.Incoming! with
        {
            Host = "other.test"
        };
        Outcome<Credential> foreignEndpoint = await credentials.ResolveAsync(account.Id, foreign, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(result.Value, Is.Null);
        Assert.That(foreignEndpoint.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(result.ToString(), Does.Not.Contain("synthetic-private-token"));
        Assert.That(imap.Connections + smtp.Connections, Is.Zero);
    }

    [Test]
    public async Task RestoreAccountsAsync_WhenBoundProfileReopensAndCredentialsAreDeleted_PreservesBindingAndDurableWork()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration configuration = Configuration(imap, smtp);
        string directory = EngineFixture.DirectoryPath();
        Guid draft = Guid.NewGuid();
        await using (SqliteProfile seed = await EngineFixture.OpenAsync(directory))
        {
            await seed.ConfigureAccountAsync(configuration.Id, configuration.Incoming!, configuration.Outgoing!, CancellationToken.None);
            await seed.SaveMailboxesAsync(configuration.Id, [EngineFixture.Mailbox()], null, CancellationToken.None);
            await seed.SaveAsync(draft, configuration.Id, await EngineFixture.BlobAsync(seed, "retained local draft"), CancellationToken.None);
        }
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        await using DesktopMailRuntime runtime = await OpenRuntimeAsync(directory, provider, imap, smtp);
        Assert.That(await runtime.BindMicrosoftAccountAsync(configuration.Id, Binding, CancellationToken.None), Is.EqualTo(Failure.None));
        await runtime.RestoreAccountsAsync(passwords, provider.Shared, provider.Shared, CancellationToken.None);

        // Act
        Failure connected = await runtime.Find(configuration.Id)!.ReconcileAsync("INBOX", CancellationToken.None);
        Failure passwordRestore = await new AccountSetup(runtime, passwords).RestoreCredentialsAsync(configuration.Id, "unused", "unused", CancellationToken.None);
        Failure removal = await runtime.RemoveAsync(configuration.Id, CancellationToken.None);
        Failure deleted = await runtime.DeleteMicrosoftCredentialsAsync(configuration.Id, CancellationToken.None);
        await runtime.DisposeAsync();
        await using DesktopMailRuntime reopened = await OpenRuntimeAsync(directory, provider, imap, smtp);
        await reopened.RestoreAccountsAsync(passwords, provider.Shared, provider.Shared, CancellationToken.None);
        Failure missing = await reopened.Find(configuration.Id)!.ReconcileAsync("INBOX", CancellationToken.None);
        AccountConfiguration retained = (await reopened.Catalog.AccountsAsync(CancellationToken.None)).Single();

        // Assert
        Assert.That(connected, Is.EqualTo(Failure.None));
        Assert.That(passwordRestore, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(removal, Is.EqualTo(Failure.NeedsReview));
        Assert.That(deleted, Is.EqualTo(Failure.None));
        Assert.That(missing, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(retained.MicrosoftBinding, Is.EqualTo(Binding));
        Assert.That(retained.ProviderKind, Is.EqualTo("Microsoft"));
        Assert.That(retained.Incoming, Is.EqualTo(configuration.Incoming!));
        Assert.That((await reopened.Catalog.DraftsAsync(configuration.Id, CancellationToken.None)).Single().Id, Is.EqualTo(draft));
        await passwords.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
        await passwords.DidNotReceiveWithAnyArgs().WriteAsync(default!, default!, default);
        Assert.That(provider.Shared.Stored, Is.Empty);
    }

    [TestCase("5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Tenant. synthetic-private", Failure.PolicyBlocked)]
    [TestCase("5.7.3 Authentication unsuccessful synthetic-private", Failure.NeedsAuthentication)]
    public async Task TestAsync_WhenExchangeRejectsAuthentication_ReturnsSafePolicyOrAuthenticationOutcome(string response, Failure expected)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        smtp.AuthenticationFailure = response;
        AccountConfiguration account = Configuration(imap, smtp);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);

        // Act
        Failure failure = await Submitter(account, credentials, smtp).TestAsync(CancellationToken.None);

        // Assert
        Assert.That(failure, Is.EqualTo(expected));
        Assert.That(failure.ToString(), Does.Not.Contain("synthetic-private"));
        Assert.That(smtp.BearerAuthentications, Has.Count.EqualTo(1));
        Assert.That(smtp.SubmissionCount, Is.Zero);
        Assert.That(provider.Exchanges, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_WhenRemoteDraftAuthFailsOrAppendIsUncertain_PreservesLocalContentAndHold(bool uncertain)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        imap.LoseAcknowledgement = uncertain;
        AccountConfiguration account = Configuration(imap, smtp);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        Guid id = Guid.NewGuid();
        DraftSnapshot draft = await profile.SaveAsync(id, account.Id, await EngineFixture.BlobAsync(profile, Mime), CancellationToken.None);
        await using ImapAdapter adapter = Adapter(account, credentials, imap);
        MailboxSnapshot draftMailbox = SentMailbox with
        {
            RemoteKey = "INBOX",
            Role = MailboxRole.Drafts
        };
        adapter.RestoreMailboxes([draftMailbox]);
        IRemoteDraftStore remote = adapter.SelectDraftMailbox("INBOX", profile.Blobs).Value!;
        if (!uncertain)
        {
            await authenticator.DeleteCredentialsAsync(CancellationToken.None);
        }

        // Act
        DraftWork work = new DraftWork(id, account.Id, profile, profile.Blobs, remote);
        Failure failure = await work.RunAsync(CancellationToken.None);
        if (uncertain)
        {
            await work.RunAsync(CancellationToken.None);
        }
        DraftSnapshot retained = (await profile.ReadAsync(id, account.Id, CancellationToken.None))!;

        // Assert
        Assert.That(failure, Is.EqualTo(uncertain ? Failure.NeedsReview : Failure.NeedsAuthentication));
        Assert.That(retained.Content, Is.EqualTo(draft.Content));
        Assert.That(retained.Held, Is.True);
        Assert.That(retained.PushedRevision, Is.LessThan(retained.Revision));
        Assert.That(imap.RemoteContent.Count, Is.EqualTo(uncertain ? 1 : 0));
        Assert.That(imap.Commands.Count(command => command.Contains(" APPEND ", StringComparison.Ordinal)), Is.EqualTo(uncertain ? 1 : 0));
    }


    [TestCase("remove")]
    [TestCase("delete")]
    [TestCase("shutdown")]
    public async Task StopAsync_WhenMsalRefreshIsActive_CancelsAndAwaitsOwnedAuthentication(string action)
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture
        {
            ExpiresIn = 30
        };
        await SeedAuthenticationAsync(provider);
        provider.BlockTokenRequests = true;
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration configuration = Configuration(imap, smtp);
        string directory = EngineFixture.DirectoryPath();
        await using (SqliteProfile seed = await EngineFixture.OpenAsync(directory))
        {
            await seed.ConfigureAccountAsync(configuration.Id, configuration.Incoming!, configuration.Outgoing!, CancellationToken.None);
            await seed.SaveMailboxesAsync(configuration.Id, [EngineFixture.Mailbox()], null, CancellationToken.None);
            Assert.That(await seed.BindMicrosoftAccountAsync(configuration.Id, Binding, CancellationToken.None), Is.EqualTo(Failure.None));
        }
        await using DesktopMailRuntime runtime = await OpenRuntimeAsync(directory, provider, imap, smtp);
        await runtime.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), provider.Shared, provider.Shared, CancellationToken.None);
        Task<Failure> pending = runtime.Find(configuration.Id)!.ReconcileAsync("INBOX", CancellationToken.None);
        await provider.TokenRequestBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        if (action == "remove")
        {
            Assert.That(await runtime.RemoveAsync(configuration.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(Failure.None));
        }
        else if (action == "delete")
        {
            Assert.That(await runtime.DeleteMicrosoftCredentialsAsync(configuration.Id, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(Failure.None));
        }
        else
        {
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Assert
        await provider.TokenRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(imap.Connections + smtp.Connections, Is.Zero);
        Assert.That(provider.Shared.Stored.Count, Is.EqualTo(action == "delete" ? 0 : 1));
    }

    [Test]
    public async Task AcquireAsync_WhenResourceTenantChanges_RejectsWithoutReplacingRetainedCache()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture
        {
            ExpiresIn = 30
        };
        await SeedAuthenticationAsync(provider);
        byte[] before = provider.Shared.Stored.Single().Value.ToArray();
        provider.ResourceTenant = "44444444-4444-4444-4444-444444444444";
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);

        // Act
        Outcome<AccessToken> result = await authenticator.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(provider.Shared.Stored.Single().Value, Is.EqualTo(before));
    }


    [Test]
    [Platform("Win")]
    public async Task AcquireAsync_WhenWindowsProtectedCacheReopens_RefreshesWithoutBrowserOrPasswordStorage()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture
        {
            ExpiresIn = 30
        };
        string directory = Path.Combine(EngineFixture.DirectoryPath(), "tokens");
        WindowsTokenStore store = new WindowsTokenStore(directory);
        await using (MicrosoftAccountAuthenticator initial = Authenticator(provider, store))
        {
            Assert.That((await initial.AcquireAsync(true, CancellationToken.None)).IsSuccess, Is.True);
        }
        provider.ExpiresIn = 3600;
        provider.AccessSecret = "synthetic-native-rotated";
        provider.RefreshSecret = "synthetic-native-refresh-rotated";
        Uri? browser = provider.Shared.Authorization;
        await using MicrosoftAccountAuthenticator reopened = Authenticator(provider, new WindowsTokenStore(directory));

        // Act
        Outcome<AccessToken> token = await reopened.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(token.Value!.Secret, Is.EqualTo(provider.AccessSecret));
        Assert.That(provider.Exchanges, Is.EqualTo(2));
        Assert.That(provider.Shared.Authorization, Is.SameAs(browser));
        Assert.That(provider.Shared.Stored, Is.Empty);
        Assert.That(File.ReadAllText(Directory.GetFiles(directory).Single()), Does.Not.Contain("synthetic-native"));
    }

    [Test]
    public async Task RunAsync_WhenPendingMutationNeedsAuthentication_RetainsIntentAndAccountIsolation()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        await authenticator.DeleteCredentialsAsync(CancellationToken.None);
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration account = Configuration(imap, smtp);
        MicrosoftTransportCredentials credentials = new MicrosoftTransportCredentials(account, authenticator, TimeProvider.System);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]), CancellationToken.None);
        MessageSnapshot message = await EngineFixture.FirstAsync(profile);
        MutationIntent intent = new MutationIntent(new OperationId(Guid.NewGuid()), account.Id, message.Id, message.Provider, MutationKind.SetRead, true, null);
        await profile.QueueAsync(intent, CancellationToken.None);
        await using ImapAdapter adapter = Adapter(account, credentials, imap);
        MutationWork work = new MutationWork(account.Id, profile, adapter, TimeProvider.System, new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));

        // Act
        Failure result = await work.RunAsync(CancellationToken.None);
        await work.RunAsync(CancellationToken.None);
        OperationEntry retained = (await profile.CommandsAsync(account.Id, CancellationToken.None)).Single().Entry;

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsAuthentication));
        Assert.That(retained.Intent, Is.EqualTo(intent));
        Assert.That(retained.State, Is.EqualTo(PendingState.NeedsAuthentication));
        Assert.That((await profile.CommandsAsync(new AccountId(2), CancellationToken.None)), Is.Empty);
        Assert.That(imap.Connections, Is.Zero);
    }

    [Test]
    public async Task BindMicrosoftAccountAsync_WhenPopulatedEfProfileReopens_RetainsWorkAndRefusesIdentitySubstitution()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using LocalMailServer imap = Server(true);
        await using LocalMailServer smtp = Server(false);
        AccountConfiguration account = Configuration(imap, smtp);
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.ConfigureAccountAsync(account.Id, account.Incoming!, account.Outgoing!, CancellationToken.None);
        await profile.CommitAsync(EngineFixture.Mailbox(), EngineFixture.Page([EngineFixture.Message(1)]), CancellationToken.None);
        Guid draft = Guid.NewGuid();
        await profile.SaveAsync(draft, account.Id, await EngineFixture.BlobAsync(profile, Mime), CancellationToken.None);
        FrozenSubmission queued = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, Mime));
        await profile.QueueAsync(queued, CancellationToken.None);
        int format = profile.AdmittedFormat;
        string migration = profile.AdmittedMigration;

        // Act
        Failure first = await profile.BindMicrosoftAccountAsync(account.Id, Binding, CancellationToken.None);
        MicrosoftAccountBinding other = Binding with
        {
            HomeAccountId = "other"
        };
        Failure changed = await profile.BindMicrosoftAccountAsync(account.Id, other, CancellationToken.None);
        await profile.DisposeAsync();
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        AccountConfiguration restored = (await reopened.AccountsAsync(CancellationToken.None)).Single();

        // Assert
        Assert.That(first, Is.EqualTo(Failure.None));
        Assert.That(changed, Is.EqualTo(Failure.NeedsReview));
        Assert.That(restored.MicrosoftBinding, Is.EqualTo(Binding));
        Assert.That(restored.ProviderKind, Is.EqualTo("Microsoft"));
        Assert.That(reopened.AdmittedFormat, Is.EqualTo(format));
        Assert.That(reopened.AdmittedMigration, Is.EqualTo(migration));
        FrozenSubmission retained = (await reopened.OutboxAsync(account.Id, CancellationToken.None)).Single().Submission;
        Assert.That(retained.Id, Is.EqualTo(queued.Id));
        Assert.That(retained.Mime, Is.EqualTo(queued.Mime));
        Assert.That(retained.Recipients, Is.EqualTo(queued.Recipients));
        Assert.That((await reopened.DraftsAsync(account.Id, CancellationToken.None)).Single().Id, Is.EqualTo(draft));
        Assert.That(await reopened.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(1));
    }

    [Test]
    public async Task AcquireAsync_WhenExplicitReconnectFollowsLocalDeletion_RetainsIdentityAndRejectsSubstitution()
    {
        // Arrange
        await using MicrosoftAuthenticationFixture provider = new MicrosoftAuthenticationFixture();
        await SeedAuthenticationAsync(provider);
        await using (MicrosoftAccountAuthenticator previous = Authenticator(provider))
        {
            await previous.DeleteCredentialsAsync(CancellationToken.None);
        }
        provider.HomeUser = "44444444-4444-4444-4444-444444444444";
        await using MicrosoftAccountAuthenticator reconnect = Authenticator(provider);

        // Act
        Outcome<AccessToken> wrong = await reconnect.AcquireAsync(true, CancellationToken.None);
        Assert.That(provider.Shared.Stored, Is.Empty);
        provider.HomeUser = MicrosoftAuthenticationFixture.User;
        Outcome<AccessToken> correct = await reconnect.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(wrong.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(correct.Value!.Subject, Is.EqualTo(Binding.HomeAccountId));
        Assert.That(correct.Value.Tenant, Is.EqualTo(Binding.TenantId));
        Assert.That(provider.Shared.Stored.Count, Is.EqualTo(1));
    }

    private static LocalMailServer Server(bool imap, string token = "synthetic-microsoft-access")
    {
        return new LocalMailServer(imap)
        {
            AdvertiseOAuth = true,
            ExpectedBearer = token
        };
    }

    private static AccountConfiguration Configuration(LocalMailServer imap, LocalMailServer smtp)
    {
        return new AccountConfiguration(
            EngineFixture.Account,
            "Synthetic Microsoft",
            new MailEndpoint("localhost", imap.Port, TlsMode.Implicit, "synthetic@example.test", "imap"),
            new MailEndpoint("localhost", smtp.Port, TlsMode.Implicit, "synthetic@example.test", "smtp"),
            ProviderKind: "Microsoft",
            MicrosoftBinding: Binding
        );
    }

    private static MicrosoftAccountAuthenticator Authenticator(MicrosoftAuthenticationFixture provider, IProtectedTokenStore? store = null)
    {
        // Real MSAL throttling survives public-client reconstruction. Keep this suite's synthetic
        // registration separate from the invalid_grant fixtures in MicrosoftAuthenticationTests.
        provider.ApplicationId = Binding.ClientId;
        return new MicrosoftAccountAuthenticator(
            EngineFixture.Account,
            Binding.ClientId,
            store ?? provider.Shared,
            provider.Shared,
            TimeProvider.System,
            TimeSpan.FromSeconds(15),
            Binding.HomeAccountId,
            Binding.TenantId
        )
        {
            HttpFactory = provider
        };
    }

    private static async Task SeedAuthenticationAsync(MicrosoftAuthenticationFixture provider)
    {
        await using MicrosoftAccountAuthenticator authenticator = Authenticator(provider);
        Assert.That((await authenticator.AcquireAsync(true, CancellationToken.None)).Failure, Is.EqualTo(Failure.None));
    }

    private static ImapAdapter Adapter(AccountConfiguration account, ITransportCredentials credentials, LocalMailServer server)
    {
        return new ImapAdapter(account.Id, account.Incoming!, credentials, 1048576, TimeProvider.System)
        {
            FixtureAuthority = server.Authority
        };
    }

    private static SmtpSubmitter Submitter(AccountConfiguration account, ITransportCredentials credentials, LocalMailServer server)
    {
        return new SmtpSubmitter(account.Id, account.Outgoing!, credentials, 1048576)
        {
            FixtureAuthority = server.Authority
        };
    }

    private static async Task<DesktopMailRuntime> OpenRuntimeAsync(
        string directory,
        MicrosoftAuthenticationFixture provider,
        LocalMailServer imap,
        LocalMailServer smtp
    )
    {
        DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(true, false, false),
            TimeProvider.System,
            new EngineLimits(2, 32, 2, 10, 1048576, 50, 0, 0, 1048576),
            CancellationToken.None
        )).Value!;
        runtime.AuthenticationHttpFactory = provider;
        runtime.ConfigureFixtureTransports = (adapter, submitter) =>
        {
            adapter.FixtureAuthority = imap.Authority;
            submitter.FixtureAuthority = smtp.Authority;
        };
        return runtime;
    }
}

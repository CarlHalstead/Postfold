using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Services.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Policies;
using Postfold.Sync.Services;
using Postfold.Sync.Stores;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Composition;

/// <summary>Explicitly opened composition root; constructing the desktop window never connects an account.</summary>
public sealed class DesktopMailRuntime : IAccountEngines, IAccountManagement, IAccountProvisioner, IAsyncDisposable
{
    private readonly SqliteProfile _profile;
    private readonly AccountScheduler _scheduler;
    private readonly IWorkPolicy _policy;
    private readonly TimeProvider _time;
    private readonly EngineLimits _limits;
    private readonly MimePipeline _mime;
    private readonly IRetryDelayPolicy _retry;
    private readonly Dictionary<AccountId, (IMailboxReader Adapter, MailAccountEngine Engine)> _accounts =
        new Dictionary<AccountId, (IMailboxReader, MailAccountEngine)>();
    private readonly SemaphoreSlim _connections = new SemaphoreSlim(1, 1);
    private readonly Dictionary<AccountId, ITransportCredentials> _credentials = new Dictionary<AccountId, ITransportCredentials>();
    private readonly Dictionary<AccountId, MicrosoftAccountAuthenticator> _authenticators = new Dictionary<AccountId, MicrosoftAccountAuthenticator>();
    private readonly Dictionary<AccountId, GoogleAccountAuthenticator> _googleAuthenticators = new Dictionary<AccountId, GoogleAccountAuthenticator>();
    private readonly Dictionary<AccountId, HttpClient> _googleHttp = new Dictionary<AccountId, HttpClient>();
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private bool _stopping;

    private DesktopMailRuntime(SqliteProfile profile, IWorkPolicy policy, TimeProvider time, EngineLimits limits)
    {
        _profile = profile;
        _policy = policy;
        _time = time;
        _limits = limits;
        _mime = new MimePipeline(limits.MaximumMimeBytes, limits.MaximumMimeParts, profile.Blobs);
        _retry = new RetryDelayPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        _scheduler = new AccountScheduler(limits.Workers, limits.QueueLimit, limits.RetryAttempts, time, policy, _retry);
    }

    public ILocalMailQuery Query => _profile;
    public IClientCatalog Catalog => _profile;
    public bool NetworkAllowed => _policy.AllowNetwork;
    public IClientAppearance Appearance => new ClientAppearance(_profile);
    public IClientIdentity Identity => new ClientIdentity(_profile);
    public IDesktopPreferenceStore DesktopPreferences => _profile;
    public IMailApplication Application => new MailApplication(
        _profile,
        _profile,
        _profile.Blobs,
        _mime,
        this,
        _limits.MaximumMimeBytes,
        _mime,
        _profile,
        _profile,
        _profile,
        _profile
    );

    internal int ProfileFormat => _profile.AdmittedFormat;
    internal string ProfileMigration => _profile.AdmittedMigration;
    internal Microsoft.Identity.Client.IMsalHttpClientFactory? AuthenticationHttpFactory { get; set; }
    internal Action<ImapAdapter, SmtpSubmitter>? ConfigureFixtureTransports { get; set; }
    internal Func<HttpClient>? GoogleHttpFactory { get; set; }

    public IMailAccountEngine? Find(AccountId account)
    {
        return _accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) entry) ? entry.Engine : null;
    }

    public Task<AccountId> NextAccountIdAsync(CancellationToken cancellationToken)
    {
        return _profile.NextAccountIdAsync(cancellationToken);
    }

    public Task<Failure> RenameAsync(AccountId account, string name, CancellationToken cancellationToken)
    {
        return _profile.RenameAccountAsync(account, name, cancellationToken);
    }

    public async Task<Failure> RemoveAsync(AccountId account, CancellationToken cancellationToken)
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            AccountConfiguration? configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Id == account);
            if (configuration is null)
            {
                return Failure.NotFound;
            }
            FolderRoles? roles = await _profile.ReadRolesAsync(account, cancellationToken).ConfigureAwait(false);
            if (_accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) previous))
            {
                await previous.Engine.DisposeAsync().ConfigureAwait(false);
                await _scheduler.SuspendAsync(account).ConfigureAwait(false);
                await previous.Adapter.DisposeAsync().ConfigureAwait(false);
            }
            // Once stopped, finish the local decision even if the originating window closes.
            Failure result = await _profile.DeactivateAccountAsync(account, CancellationToken.None).ConfigureAwait(false);
            if (result != Failure.None)
            {
                _scheduler.Resume(account);
                _credentials.TryGetValue(account, out ITransportCredentials? credentials);
                ImmutableArray<MailboxSnapshot> mailboxes = (await _profile.MailboxesAsync(CancellationToken.None)
                    .ConfigureAwait(false)).Where(item => item.Account == account).ToImmutableArray();
                _accounts[account] = CreateRestoredAccount(configuration, mailboxes, roles, credentials);
                return result;
            }
            _accounts.Remove(account);
            _credentials.Remove(account);
            if (_authenticators.Remove(account, out MicrosoftAccountAuthenticator? authenticator))
            {
                await authenticator.DisposeAsync().ConfigureAwait(false);
            }
            if (_googleAuthenticators.Remove(account, out GoogleAccountAuthenticator? google))
            {
                await google.DisposeAsync().ConfigureAwait(false);
            }
            if (_googleHttp.Remove(account, out HttpClient? http))
            {
                http.Dispose();
            }

            return Failure.None;
        }
        finally
        {
            _connections.Release();
        }
    }

    public Task<Failure> TestOutgoingConnectionAsync(
        AccountId account,
        MailEndpoint outgoing,
        ICredentialStore credentials,
        CancellationToken cancellationToken
    )
    {
        return _policy.AllowNetwork ? new SmtpSubmitter(account, outgoing, credentials).TestAsync(cancellationToken) :
            Task.FromResult(Failure.PolicyBlocked);
    }

    public Task RestoreAccountsAsync(ICredentialStore credentials, CancellationToken cancellationToken)
    {
        return RestoreAccountsAsync(credentials, null, null, cancellationToken);
    }

    /// <summary>Restores retained provider bindings. Microsoft acquisition is silent and uses a separate protected token store.</summary>
    public async Task RestoreAccountsAsync(
        ICredentialStore credentials,
        IProtectedTokenStore? tokens,
        ISystemBrowser? browser,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _connections.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ImmutableArray<MailboxSnapshot> mailboxes = await _profile.MailboxesAsync(linked.Token).ConfigureAwait(false);
            foreach (AccountConfiguration account in await _profile.AccountsAsync(linked.Token).ConfigureAwait(false))
            {
                if (_accounts.ContainsKey(account.Id))
                {
                    continue;
                }

                ImmutableArray<MailboxSnapshot> cached = mailboxes.Where(item => item.Account == account.Id).ToImmutableArray();
                FolderRoles? roles = await _profile.ReadRolesAsync(account.Id, linked.Token).ConfigureAwait(false);
                if (account.ProviderKind == "Google")
                {
                    CreateGoogleAuthenticator(account, tokens, browser);
                    _accounts.Add(account.Id, CreateRestoredAccount(account, cached, roles, null));
                    continue;
                }
                ITransportCredentials source = CreateCredentials(account, credentials, tokens, browser);
                _accounts.Add(account.Id, CreateRestoredAccount(account, cached, roles, source));
                _credentials.Add(account.Id, source);
            }
        }
        finally
        {
            _connections.Release();
        }
    }

    public static async Task<Outcome<DesktopMailRuntime>> OpenAsync(
        string directory,
        IWorkPolicy policy,
        TimeProvider time,
        EngineLimits limits,
        CancellationToken cancellationToken
    )
    {
        if (limits.Workers < 2 || limits.QueueLimit < 1 || limits.RetryAttempts < 1 ||
            limits.MaximumMimeBytes < 1 || limits.MaximumMimeParts < 1 ||
            limits.MetadataBatch is < 1 or > 1000 || limits.PrefetchDays < 0 ||
            limits.PrefetchBatch is < 0 or > 200 || limits.CacheBudgetBytes < 0)
        {
            return Outcome<DesktopMailRuntime>.Fail(Failure.Validation);
        }

        Outcome<SqliteProfile> opened = await SqliteProfile.OpenAsync(
            Path.Combine(directory, "profile.db"),
            time,
            cancellationToken
        ).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Outcome<DesktopMailRuntime>.Fail(opened.Failure);
        }

        SqliteProfile profile = opened.Value!;
        try
        {
            await profile.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return Outcome<DesktopMailRuntime>.Success(new DesktopMailRuntime(profile, policy, time, limits));
        }
        catch
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Outcome<IMailAccountEngine>> ConnectAccountAsync(
        AccountId account,
        string displayName,
        MailEndpoint incoming,
        MailEndpoint outgoing,
        ICredentialStore credentials,
        string? selectedDraftMailbox,
        CancellationToken cancellationToken
    )
    {
        if (!_policy.AllowNetwork)
        {
            return Outcome<IMailAccountEngine>.Fail(Failure.Unavailable);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token
        );
        await _connections.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_accounts.ContainsKey(account))
            {
                return Outcome<IMailAccountEngine>.Fail(Failure.Validation);
            }

            if ((await _profile.AccountsAsync(linked.Token).ConfigureAwait(false))
                .Any(item => item.Id == account && item.ProviderKind != "Imap"))
            {
                return Outcome<IMailAccountEngine>.Fail(Failure.NeedsReview);
            }

            ImapAdapter adapter = new ImapAdapter(account, incoming, credentials, _limits.MaximumMimeBytes, _time);
            Outcome<ImmutableArray<MailboxSnapshot>> discovered;
            try
            {
                discovered = await adapter.DiscoverAsync(linked.Token).ConfigureAwait(false);
            }
            catch
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            if (!discovered.IsSuccess)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
                return Outcome<IMailAccountEngine>.Fail(discovered.Failure);
            }

            IRemoteDraftStore? drafts = null;
            MailboxSnapshot[] draftFolders = discovered.Value.Where(item =>
                item.Role == MailboxRole.Drafts && item.Selectable && item.Writable).ToArray();
            selectedDraftMailbox ??= draftFolders.Length == 1 ? draftFolders[0].RemoteKey : null;
            if (selectedDraftMailbox is not null)
            {
                Outcome<IRemoteDraftStore> selection = adapter.SelectDraftMailbox(selectedDraftMailbox, _profile.Blobs);
                if (!selection.IsSuccess)
                {
                    await adapter.DisposeAsync().ConfigureAwait(false);
                    return Outcome<IMailAccountEngine>.Fail(selection.Failure);
                }

                drafts = new ComposeRemoteDraftStore(selection.Value!, _mime);
            }

            SmtpSubmitter smtp = new SmtpSubmitter(account, outgoing, credentials, _limits.MaximumMimeBytes);
            await _profile.AddAccountAsync(account, displayName, linked.Token).ConfigureAwait(false);
            await _profile.ConfigureAccountAsync(account, incoming, outgoing, linked.Token).ConfigureAwait(false);
            await _profile.SaveMailboxesAsync(account, discovered.Value, null, linked.Token).ConfigureAwait(false);
            await _profile.SaveCapabilitiesAsync(account, adapter.Capabilities, linked.Token).ConfigureAwait(false);
            ISentCopyStore? sent = discovered.Value.Count(item => item.Role == MailboxRole.Sent && item.Selectable && item.Writable) == 1 ? adapter : null;
            MailAccountEngine engine = new MailAccountEngine(
                adapter,
                adapter,
                adapter,
                drafts,
                sent,
                smtp,
                _profile,
                _profile,
                _profile,
                _profile,
                _profile,
                _profile.Blobs,
                _mime,
                _scheduler,
                _policy,
                _retry,
                _time,
                _limits,
                _profile,
                discovered.Value,
                adapter.CreateIdleSignal()
            );
            _accounts.Add(account, (adapter, engine));
            _credentials.Add(account, new PasswordTransportCredentials(credentials));
            return Outcome<IMailAccountEngine>.Success(engine);
        }
        finally
        {
            _connections.Release();
        }
    }

    public async Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken)
    {
        if (!roles.IsValid)
        {
            return Failure.Validation;
        }

        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) entry))
            {
                return Failure.NotFound;
            }
            if (entry.Adapter is not ImapAdapter imap)
            {
                return Failure.Unsupported;
            }

            string?[] selected = [roles.Sent, roles.Drafts, roles.Archive, roles.Trash];
            ImmutableArray<MailboxSnapshot> mailboxes = entry.Engine.Mailboxes;
            if (selected.Any(key => key is not null && !mailboxes.Any(item => item.RemoteKey == key &&
                item.Selectable && item.Writable && item.Role != MailboxRole.Inbox)))
            {
                return Failure.NeedsReview;
            }

            AccountConfiguration configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == account);
            await _profile.SaveMailboxesAsync(account, mailboxes, roles, cancellationToken).ConfigureAwait(false);
            mailboxes = mailboxes.Select(roles.Apply).ToImmutableArray();
            IRemoteDraftStore? drafts = roles.Drafts is null ? null :
                imap.SelectDraftMailbox(roles.Drafts, _profile.Blobs).Value;
            SmtpSubmitter smtp = new SmtpSubmitter(
                account,
                configuration.Outgoing,
                _credentials[account],
                _limits.MaximumMimeBytes
            );
            MailAccountEngine engine = new MailAccountEngine(
                imap,
                imap,
                imap,
                drafts is null ? null : new ComposeRemoteDraftStore(drafts, _mime),
                roles.Sent is null ? null : imap,
                smtp,
                _profile,
                _profile,
                _profile,
                _profile,
                _profile,
                _profile.Blobs,
                _mime,
                _scheduler,
                _policy,
                _retry,
                _time,
                _limits,
                _profile,
                mailboxes,
                imap.CreateIdleSignal()
            );
            await entry.Engine.DisposeAsync().ConfigureAwait(false);
            imap.SetRoles(roles);
            _accounts[account] = (entry.Adapter, engine);
            return Failure.None;
        }
        finally
        {
            _connections.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _connections.WaitAsync().ConfigureAwait(false);
        try
        {
            await _scheduler.DisposeAsync().ConfigureAwait(false);
            foreach ((IMailboxReader adapter, MailAccountEngine engine) in _accounts.Values)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
                await adapter.DisposeAsync().ConfigureAwait(false);
            }

            foreach (MicrosoftAccountAuthenticator authenticator in _authenticators.Values)
            {
                await authenticator.DisposeAsync().ConfigureAwait(false);
            }
            foreach (GoogleAccountAuthenticator authenticator in _googleAuthenticators.Values)
            {
                await authenticator.DisposeAsync().ConfigureAwait(false);
            }
            foreach (HttpClient http in _googleHttp.Values)
            {
                http.Dispose();
            }

            await _profile.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _connections.Release();
        }
    }

    public async Task<Failure> ReplaceCredentialsAsync(
        AccountId account,
        ICredentialStore credentials,
        CancellationToken cancellationToken
    )
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) previous))
            {
                return Failure.NotFound;
            }

            AccountConfiguration configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == account);
            if (configuration.ProviderKind != "Imap")
            {
                return Failure.NeedsAuthentication;
            }

            ImmutableArray<MailboxSnapshot> mailboxes = previous.Engine.Mailboxes;
            FolderRoles? roles = await _profile.ReadRolesAsync(account, cancellationToken).ConfigureAwait(false);
            await previous.Engine.DisposeAsync().ConfigureAwait(false);
            await previous.Adapter.DisposeAsync().ConfigureAwait(false);
            ITransportCredentials source = new PasswordTransportCredentials(credentials);
            _accounts[account] = CreateRestoredAccount(configuration, mailboxes, roles, source);
            _credentials[account] = source;
            return Failure.None;
        }
        finally
        {
            _connections.Release();
        }
    }

    // Synthetic engineering entry point only; ordinary setup cannot create a provider account yet.
    internal async Task<Failure> BindMicrosoftAccountAsync(
        AccountId account,
        MicrosoftAccountBinding binding,
        CancellationToken cancellationToken
    )
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            return _accounts.ContainsKey(account) ? Failure.NeedsReview :
                await _profile.BindMicrosoftAccountAsync(account, binding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connections.Release();
        }
    }

    /// <summary>Stops and drains account work before local cache removal. Retains binding, mail and unresolved work; does not revoke a server grant.</summary>
    internal async Task<Failure> DeleteMicrosoftCredentialsAsync(AccountId account, CancellationToken cancellationToken)
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_authenticators.TryGetValue(account, out MicrosoftAccountAuthenticator? authenticator) ||
                !_accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) previous))
            {
                return Failure.NotFound;
            }

            AccountConfiguration configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == account);
            FolderRoles? roles = await _profile.ReadRolesAsync(account, cancellationToken).ConfigureAwait(false);
            ImmutableArray<MailboxSnapshot> mailboxes = previous.Engine.Mailboxes;
            await previous.Engine.DisposeAsync().ConfigureAwait(false);
            await _scheduler.SuspendAsync(account).ConfigureAwait(false);
            await previous.Adapter.DisposeAsync().ConfigureAwait(false);
            try
            {
                Outcome<bool> deleted = await authenticator.DeleteCredentialsAsync(cancellationToken).ConfigureAwait(false);
                return deleted.Failure;
            }
            finally
            {
                _accounts[account] = CreateRestoredAccount(configuration, mailboxes, roles, _credentials[account]);
                _scheduler.Resume(account);
            }
        }
        finally
        {
            _connections.Release();
        }
    }

    /// <summary>Stops new scheduled network work and waits for active work without cancelling a submission.</summary>
    internal Task PauseWorkAsync(CancellationToken cancellationToken)
    {
        return _scheduler.PauseAsync(cancellationToken);
    }

    internal void ResumeWork()
    {
        _scheduler.ResumeAll();
    }

    internal Task<bool> HasActiveSubmissionsAsync(CancellationToken cancellationToken)
    {
        return _profile.HasActiveSubmissionsAsync(cancellationToken);
    }

    // Internal until separately scoped provider setup/reconnect acceptance.
    internal async Task<Failure> BindGoogleAccountAsync(
        AccountId account,
        GoogleAccountBinding binding,
        CancellationToken cancellationToken
    )
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            return _accounts.ContainsKey(account) ? Failure.NeedsReview :
                await _profile.BindGoogleAccountAsync(account, binding, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connections.Release();
        }
    }

    /// <summary>Drains account work before removing only local Google token bytes; retained binding and mail survive.</summary>
    internal async Task<Failure> DeleteGoogleCredentialsAsync(
        AccountId account,
        CancellationToken cancellationToken
    )
    {
        await _connections.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_googleAuthenticators.TryGetValue(account, out GoogleAccountAuthenticator? authenticator) ||
                !_accounts.TryGetValue(account, out (IMailboxReader Adapter, MailAccountEngine Engine) previous))
            {
                return Failure.NotFound;
            }
            AccountConfiguration configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false)).Single(item => item.Id == account);
            ImmutableArray<MailboxSnapshot> mailboxes = previous.Engine.Mailboxes;
            await previous.Engine.DisposeAsync().ConfigureAwait(false);
            await _scheduler.SuspendAsync(account).ConfigureAwait(false);
            await previous.Adapter.DisposeAsync().ConfigureAwait(false);
            try
            {
                return (await authenticator.DeleteCredentialsAsync(cancellationToken).ConfigureAwait(false)).Failure;
            }
            finally
            {
                _accounts[account] = CreateGoogleAccount(configuration, mailboxes);
                _scheduler.Resume(account);
            }
        }
        finally
        {
            _connections.Release();
        }
    }

    private (IMailboxReader Adapter, MailAccountEngine Engine) CreateRestoredAccount(
        AccountConfiguration account,
        ImmutableArray<MailboxSnapshot> mailboxes,
        FolderRoles? roles,
        ITransportCredentials? credentials
    )
    {
        if (account.ProviderKind == "Google")
        {
            return CreateGoogleAccount(account, mailboxes);
        }
        ArgumentNullException.ThrowIfNull(credentials);
        ImapAdapter adapter = new ImapAdapter(account.Id, account.Incoming, credentials, _limits.MaximumMimeBytes, _time);
        adapter.RestoreMailboxes(mailboxes);
        adapter.RestoreCapabilities(account.Capabilities);
        adapter.SetRoles(roles);
        MailboxSnapshot[] draftFolders = mailboxes.Where(item => item.Role == MailboxRole.Drafts &&
            item.Selectable && item.Writable).ToArray();
        IRemoteDraftStore? drafts = draftFolders.Length == 1 ?
            adapter.SelectDraftMailbox(draftFolders[0].RemoteKey, _profile.Blobs).Value : null;
        ISentCopyStore? sent = mailboxes.Count(item => item.Role == MailboxRole.Sent && item.Selectable && item.Writable) == 1 ?
            adapter : null;
        SmtpSubmitter smtp = new SmtpSubmitter(account.Id, account.Outgoing, credentials, _limits.MaximumMimeBytes);
        ConfigureFixtureTransports?.Invoke(adapter, smtp);
        MailAccountEngine engine = new MailAccountEngine(
            adapter,
            adapter,
            adapter,
            drafts is null ? null : new ComposeRemoteDraftStore(drafts, _mime),
            sent,
            smtp,
            _profile,
            _profile,
            _profile,
            _profile,
            _profile,
            _profile.Blobs,
            _mime,
            _scheduler,
            _policy,
            _retry,
            _time,
            _limits,
            _profile,
            mailboxes
        );
        return (adapter, engine);
    }

    private void CreateGoogleAuthenticator(
        AccountConfiguration account,
        IProtectedTokenStore? tokens,
        ISystemBrowser? browser
    )
    {
        if (account.GoogleBinding is not { IsValid: true } binding || tokens is null || browser is null)
        {
            return;
        }
        GoogleAccountAuthenticator authenticator = new GoogleAccountAuthenticator(
            account.Id,
            binding.ClientId,
            tokens,
            browser,
            GoogleHttp(account.Id),
            _time,
            TimeSpan.FromSeconds(30),
            binding.Address
        );
        _googleAuthenticators.Add(account.Id, authenticator);
    }

    private (IMailboxReader Adapter, MailAccountEngine Engine) CreateGoogleAccount(
        AccountConfiguration account,
        ImmutableArray<MailboxSnapshot> mailboxes
    )
    {
        _googleAuthenticators.TryGetValue(account.Id, out GoogleAccountAuthenticator? authentication);
        GoogleAccountBinding binding = account.GoogleBinding ?? new GoogleAccountBinding("", "");
        GmailAdapter adapter = new GmailAdapter(
            account.Id,
            binding,
            authentication,
            _profile.Blobs,
            _limits.MaximumMimeBytes,
            _time,
            GoogleHttp(account.Id),
            false
        );
        MailAccountEngine engine = new MailAccountEngine(
            adapter,
            adapter,
            adapter,
            new ComposeRemoteDraftStore(adapter, _mime),
            adapter,
            adapter,
            _profile,
            _profile,
            _profile,
            _profile,
            _profile,
            _profile.Blobs,
            _mime,
            _scheduler,
            _policy,
            _retry,
            _time,
            _limits,
            _profile,
            mailboxes
        );
        return (adapter, engine);
    }

    private HttpClient GoogleHttp(AccountId account)
    {
        if (!_googleHttp.TryGetValue(account, out HttpClient? http))
        {
            http = GoogleHttpFactory?.Invoke() ?? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
            _googleHttp.Add(account, http);
        }
        return http;
    }

    private ITransportCredentials CreateCredentials(
        AccountConfiguration account,
        ICredentialStore passwords,
        IProtectedTokenStore? tokens,
        ISystemBrowser? browser
    )
    {
        if (account.ProviderKind == "Imap")
        {
            return new PasswordTransportCredentials(passwords);
        }

        MicrosoftAccountAuthenticator? authenticator = null;
        if (account.ProviderKind == "Microsoft" && account.MicrosoftBinding is { IsValid: true } binding &&
            tokens is not null && browser is not null)
        {
            authenticator = new MicrosoftAccountAuthenticator(
                account.Id,
                binding.ClientId,
                tokens,
                browser,
                _time,
                TimeSpan.FromSeconds(30),
                binding.HomeAccountId,
                binding.TenantId
            )
            {
                HttpFactory = AuthenticationHttpFactory
            };
            _authenticators.Add(account.Id, authenticator);
        }

        return new MicrosoftTransportCredentials(account, authenticator, _time);
    }
}

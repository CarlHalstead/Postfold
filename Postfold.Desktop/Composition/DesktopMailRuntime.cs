using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Synchronisation;
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
    private readonly Dictionary<AccountId, (ImapAdapter Adapter, MailAccountEngine Engine)> _accounts =
        new Dictionary<AccountId, (ImapAdapter, MailAccountEngine)>();
    private readonly SemaphoreSlim _connections = new SemaphoreSlim(1, 1);
    private readonly Dictionary<AccountId, ICredentialStore> _credentials = new Dictionary<AccountId, ICredentialStore>();
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

    public IMailAccountEngine? Find(AccountId account)
    {
        return _accounts.TryGetValue(account, out (ImapAdapter Adapter, MailAccountEngine Engine) entry) ? entry.Engine : null;
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
            if (_accounts.TryGetValue(account, out (ImapAdapter Adapter, MailAccountEngine Engine) previous))
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
                if (_credentials.TryGetValue(account, out ICredentialStore? credentials))
                {
                    ImmutableArray<MailboxSnapshot> mailboxes = (await _profile.MailboxesAsync(CancellationToken.None)
                        .ConfigureAwait(false)).Where(item => item.Account == account).ToImmutableArray();
                    _accounts[account] = CreateRestoredAccount(configuration, mailboxes, roles, credentials);
                }
                return result;
            }
            _accounts.Remove(account);
            _credentials.Remove(account);
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

    public async Task RestoreAccountsAsync(ICredentialStore credentials, CancellationToken cancellationToken)
    {
        ImmutableArray<MailboxSnapshot> mailboxes = await _profile.MailboxesAsync(cancellationToken).ConfigureAwait(false);
        foreach (AccountConfiguration account in await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            ImmutableArray<MailboxSnapshot> cached = mailboxes.Where(item => item.Account == account.Id).ToImmutableArray();
            FolderRoles? roles = await _profile.ReadRolesAsync(account.Id, cancellationToken).ConfigureAwait(false);
            _accounts.Add(account.Id, CreateRestoredAccount(account, cached, roles, credentials));
            _credentials.Add(account.Id, credentials);
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
            ISentCopyStore? sent = discovered.Value.Count(item => item.Role == MailboxRole.Sent) == 1 ? adapter : null;
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
            _credentials.Add(account, credentials);
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
            if (!_accounts.TryGetValue(account, out (ImapAdapter Adapter, MailAccountEngine Engine) entry))
            {
                return Failure.NotFound;
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
                entry.Adapter.SelectDraftMailbox(roles.Drafts, _profile.Blobs).Value;
            SmtpSubmitter smtp = new SmtpSubmitter(
                account,
                configuration.Outgoing,
                _credentials[account],
                _limits.MaximumMimeBytes
            );
            MailAccountEngine engine = new MailAccountEngine(
                entry.Adapter,
                entry.Adapter,
                entry.Adapter,
                drafts is null ? null : new ComposeRemoteDraftStore(drafts, _mime),
                roles.Sent is null ? null : entry.Adapter,
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
                entry.Adapter.CreateIdleSignal()
            );
            await entry.Engine.DisposeAsync().ConfigureAwait(false);
            entry.Adapter.SetRoles(roles);
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
            foreach ((ImapAdapter adapter, MailAccountEngine engine) in _accounts.Values)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
                await adapter.DisposeAsync().ConfigureAwait(false);
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
            if (!_accounts.TryGetValue(account, out (ImapAdapter Adapter, MailAccountEngine Engine) previous))
            {
                return Failure.NotFound;
            }

            AccountConfiguration configuration = (await _profile.AccountsAsync(cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == account);
            ImmutableArray<MailboxSnapshot> mailboxes = previous.Engine.Mailboxes;
            FolderRoles? roles = await _profile.ReadRolesAsync(account, cancellationToken).ConfigureAwait(false);
            await previous.Engine.DisposeAsync().ConfigureAwait(false);
            await previous.Adapter.DisposeAsync().ConfigureAwait(false);
            _accounts[account] = CreateRestoredAccount(configuration, mailboxes, roles, credentials);
            _credentials[account] = credentials;
            return Failure.None;
        }
        finally
        {
            _connections.Release();
        }
    }

    private (ImapAdapter Adapter, MailAccountEngine Engine) CreateRestoredAccount(
        AccountConfiguration account,
        ImmutableArray<MailboxSnapshot> mailboxes,
        FolderRoles? roles,
        ICredentialStore credentials
    )
    {
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
}

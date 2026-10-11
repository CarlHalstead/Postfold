using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;
using Postfold.Protocols;
using Postfold.Storage;
using Postfold.Sync;

namespace Postfold.Desktop;

/// <summary>Explicitly opened composition root; constructing the desktop window never connects an account.</summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly SqliteProfile _profile;
    private readonly AccountScheduler _scheduler;
    private readonly IWorkPolicy _policy;
    private readonly TimeProvider _time;
    private readonly EngineLimits _limits;
    private readonly IMimePipeline _mime;
    private readonly IRetryDelayPolicy _retry;
    private readonly Dictionary<AccountId, (ImapAdapter Adapter, MailAccountEngine Engine)> _accounts =
        new Dictionary<AccountId, (ImapAdapter, MailAccountEngine)>();
    private readonly SemaphoreSlim _connections = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
    private bool _stopping;

    private EngineHost(SqliteProfile profile, IWorkPolicy policy, TimeProvider time, EngineLimits limits)
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

    public static async Task<Outcome<EngineHost>> OpenAsync(string directory, IWorkPolicy policy,
        TimeProvider time, EngineLimits limits, CancellationToken cancellationToken)
    {
        if (limits.Workers < 2 || limits.QueueLimit < 1 || limits.RetryAttempts < 1 ||
            limits.MaximumMimeBytes < 1 || limits.MaximumMimeParts < 1 ||
            limits.MetadataBatch is < 1 or > 1000 || limits.PrefetchDays < 0 ||
            limits.PrefetchBatch is < 0 or > 200 || limits.CacheBudgetBytes < 0)
        {
            return Outcome<EngineHost>.Fail(Failure.Validation);
        }

        Outcome<SqliteProfile> opened = await SqliteProfile.OpenAsync(Path.Combine(directory, "profile.db"), time,
            cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Outcome<EngineHost>.Fail(opened.Failure);
        }

        SqliteProfile profile = opened.Value!;
        try
        {
            await profile.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return Outcome<EngineHost>.Success(new EngineHost(profile, policy, time, limits));
        }
        catch
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Outcome<IMailAccountEngine>> ConnectAccountAsync(AccountId account, string displayName,
        MailEndpoint incoming, MailEndpoint outgoing, ICredentialStore credentials, string? selectedDraftMailbox,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        await _connections.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_accounts.ContainsKey(account))
            {
                return Outcome<IMailAccountEngine>.Fail(Failure.Validation);
            }

            await _profile.AddAccountAsync(account, displayName, linked.Token).ConfigureAwait(false);
            await _profile.ConfigureAccountAsync(account, incoming, outgoing, linked.Token).ConfigureAwait(false);
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

                drafts = selection.Value;
            }

            SmtpSubmitter smtp = new SmtpSubmitter(account, outgoing, credentials, _limits.MaximumMimeBytes);
            ISentCopyStore? sent = discovered.Value.Count(item => item.Role == MailboxRole.Sent) == 1 ? adapter : null;
            MailAccountEngine engine = new MailAccountEngine(adapter, adapter, adapter, drafts, sent, smtp,
                _profile, _profile, _profile, _profile, _profile, _profile.Blobs, _mime, _scheduler,
                _policy, _retry, _time, _limits, _profile, discovered.Value, adapter.CreateIdleSignal());
            _accounts.Add(account, (adapter, engine));
            return Outcome<IMailAccountEngine>.Success(engine);
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
}

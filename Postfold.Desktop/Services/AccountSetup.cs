using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

public sealed class AccountSetup : IAccountSetup
{
    private readonly IAccountProvisioner _host;
    private readonly ICredentialStore _secure;
    private readonly IClientIdentity? _identity;
    private readonly SemaphoreSlim _additions = new SemaphoreSlim(1, 1);
    private readonly IAccountServerDiscovery _discovery;

    public AccountSetup(
        IAccountProvisioner host,
        ICredentialStore secure,
        IClientIdentity? identity = null,
        IAccountServerDiscovery? discovery = null
    )
    {
        _host = host;
        _secure = secure;
        _identity = identity;
        _discovery = discovery ?? new MailServerDiscovery();
    }

    public Task<AccountServerSettings> DiscoverAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _host.NetworkAllowed ? _discovery.DiscoverAsync(email, cancellationToken) :
            Task.FromResult(new AccountServerSettings(null, null));
    }

    public async Task<AccountSetupResult> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken)
    {
        await _additions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AddCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _additions.Release();
        }
    }

    public async Task<Failure> RestoreCredentialsAsync(
        AccountId account,
        string incomingPassword,
        string outgoingPassword,
        CancellationToken cancellationToken
    )
    {
        AccountConfiguration? configuration = null;
        foreach (AccountConfiguration candidate in await _host.Catalog.AccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (candidate.Id == account)
            {
                configuration = candidate;
                break;
            }
        }

        if (configuration is null)
        {
            return Failure.NotFound;
        }

        if (configuration.ProviderKind != "Imap")
        {
            return Failure.NeedsAuthentication;
        }

        Outcome<bool> incoming = await _secure.WriteAsync(
            configuration.Incoming.CredentialKey,
            new Credential(incomingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<bool> outgoing = await _secure.WriteAsync(
            configuration.Outgoing.CredentialKey,
            new Credential(outgoingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        if (!incoming.IsSuccess || !outgoing.IsSuccess)
        {
            return Failure.CredentialStorageUnavailable;
        }

        return await _host.ReplaceCredentialsAsync(account, _secure, cancellationToken).ConfigureAwait(false);
    }

    public Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken)
    {
        return _host.SetRolesAsync(account, roles, cancellationToken);
    }

    private async Task<AccountSetupResult> AddCoreAsync(AccountSetupRequest request, CancellationToken cancellationToken)
    {
        if (!_host.NetworkAllowed)
        {
            return new AccountSetupResult(Failure.PolicyBlocked, Failure.PolicyBlocked, Failure.PolicyBlocked, []);
        }

        if (!Valid(request.Incoming) || !Valid(request.Outgoing) || string.IsNullOrWhiteSpace(request.Name) ||
            request.Sender is not null && (!MailAddress.TryCreate(request.Sender, out MailAddress? sender) ||
                sender.Address != request.Sender || _identity is null))
        {
            return new AccountSetupResult(Failure.Validation, Failure.Validation, Failure.Validation, []);
        }

        ImmutableArray<AccountConfiguration> accounts = await _host.Catalog.AccountsAsync(
            cancellationToken
        ).ConfigureAwait(false);
        string address = request.Sender ?? request.Incoming.Username;
        if (accounts.Any(account => string.Equals(account.Identity?.Sender ?? account.Incoming.Username,
            address, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(account.Incoming.Host, request.Incoming.Host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(account.Incoming.Username, request.Incoming.Username, StringComparison.OrdinalIgnoreCase)))
        {
            return new AccountSetupResult(Failure.Validation, Failure.Validation, Failure.Validation, [], Duplicate: true);
        }

        AccountId id = await _host.NextAccountIdAsync(cancellationToken).ConfigureAwait(false);
        string incomingKey = Guid.NewGuid().ToString("N");
        string outgoingKey = Guid.NewGuid().ToString("N");
        MailEndpoint incoming = request.Incoming with
        {
            CredentialKey = incomingKey
        };
        MailEndpoint outgoing = request.Outgoing with
        {
            CredentialKey = outgoingKey
        };
        Outcome<bool> first = await _secure.WriteAsync(
            incomingKey,
            new Credential(request.IncomingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<bool> second = await _secure.WriteAsync(
            outgoingKey,
            new Credential(request.OutgoingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        if (!first.IsSuccess || !second.IsSuccess)
        {
            return new AccountSetupResult(Failure.CredentialStorageUnavailable, Failure.Unavailable, Failure.Unavailable, []);
        }

        Failure outgoingResult = await _host.TestOutgoingConnectionAsync(
            id,
            outgoing,
            _secure,
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<IMailAccountEngine> engine = await _host.ConnectAccountAsync(
            id,
            request.Name,
            incoming,
            outgoing,
            _secure,
            request.DraftMailbox,
            cancellationToken
        ).ConfigureAwait(false);
        Failure identityResult = engine.IsSuccess && request.Sender is not null ?
            await _identity!.SaveAsync(id, new ComposeIdentity(request.Sender), cancellationToken).ConfigureAwait(false) :
            Failure.None;
        Failure synchronisation = Failure.None;
        if (engine.IsSuccess)
        {
            foreach (MailboxSnapshot mailbox in engine.Value!.Mailboxes.Where(item => item.Selectable)
                .OrderByDescending(item => item.Role == Postfold.Core.Enums.MailboxRole.Inbox))
            {
                synchronisation = await engine.Value.ReconcileAsync(mailbox.RemoteKey, cancellationToken).ConfigureAwait(false);
                if (synchronisation != Failure.None)
                {
                    break;
                }
            }
        }
        return new AccountSetupResult(Failure.None, engine.Failure, outgoingResult, engine.Value?.Mailboxes ?? [],
            identityResult, synchronisation);
    }

    private static bool Valid(MailEndpoint endpoint)
    {
        return endpoint.Port is > 0 and <= 65535 && Enum.IsDefined(endpoint.Tls) &&
            Uri.CheckHostName(endpoint.Host) != UriHostNameType.Unknown && !string.IsNullOrWhiteSpace(endpoint.Username);
    }
}

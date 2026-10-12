using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

public sealed class AccountSetup : IAccountSetup
{
    private readonly IAccountProvisioner _host;
    private readonly ICredentialStore _secure;
    private readonly ICredentialStore _session;

    public AccountSetup(IAccountProvisioner host, ICredentialStore secure, ICredentialStore session)
    {
        _host = host;
        _secure = secure;
        _session = session;
    }

    public async Task<AccountSetupResult> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken)
    {
        if (!_host.NetworkAllowed)
        {
            return new AccountSetupResult(Failure.PolicyBlocked, Failure.PolicyBlocked, Failure.PolicyBlocked, []);
        }

        if (!Valid(request.Incoming) || !Valid(request.Outgoing) || string.IsNullOrWhiteSpace(request.Name))
        {
            return new AccountSetupResult(Failure.Validation, Failure.Validation, Failure.Validation, []);
        }

        ICredentialStore store = request.SessionOnly ? _session : _secure;
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
        Outcome<bool> first = await store.WriteAsync(
            incomingKey,
            new Credential(request.IncomingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<bool> second = await store.WriteAsync(
            outgoingKey,
            new Credential(request.OutgoingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        if (!first.IsSuccess || !second.IsSuccess)
        {
            return new AccountSetupResult(Failure.CredentialStorageUnavailable, Failure.Unavailable, Failure.Unavailable, []);
        }

        ImmutableArray<AccountConfiguration> accounts = await _host.Catalog.AccountsAsync(
            cancellationToken
        ).ConfigureAwait(false);
        long next = 1;
        foreach (AccountConfiguration account in accounts)
        {
            next = Math.Max(next, account.Id.Value + 1);
        }

        AccountId id = new AccountId(next);
        Failure outgoingResult = await _host.TestOutgoingConnectionAsync(
            id,
            outgoing,
            store,
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<IMailAccountEngine> engine = await _host.ConnectAccountAsync(
            id,
            request.Name,
            incoming,
            outgoing,
            store,
            request.DraftMailbox,
            cancellationToken
        ).ConfigureAwait(false);
        return new AccountSetupResult(Failure.None, engine.Failure, outgoingResult, engine.Value?.Mailboxes ?? []);
    }

    public async Task<Failure> RestoreCredentialsAsync(
        AccountId account,
        string incomingPassword,
        string outgoingPassword,
        bool sessionOnly,
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

        ICredentialStore store = sessionOnly ? _session : _secure;
        Outcome<bool> incoming = await store.WriteAsync(
            configuration.Incoming.CredentialKey,
            new Credential(incomingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        Outcome<bool> outgoing = await store.WriteAsync(
            configuration.Outgoing.CredentialKey,
            new Credential(outgoingPassword, false),
            cancellationToken
        ).ConfigureAwait(false);
        if (!incoming.IsSuccess || !outgoing.IsSuccess)
        {
            return Failure.CredentialStorageUnavailable;
        }

        return await _host.ReplaceCredentialsAsync(account, store, cancellationToken).ConfigureAwait(false);
    }

    public Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken)
    {
        return _host.SetRolesAsync(account, roles, cancellationToken);
    }

    private static bool Valid(MailEndpoint endpoint)
    {
        return endpoint.Port is > 0 and <= 65535 && Enum.IsDefined(endpoint.Tls) &&
            Uri.CheckHostName(endpoint.Host) != UriHostNameType.Unknown && !string.IsNullOrWhiteSpace(endpoint.Username);
    }
}

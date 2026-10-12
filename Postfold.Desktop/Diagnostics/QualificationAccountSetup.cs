using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Diagnostics;

/// <summary>Authored form responses; never stores credentials, creates an account or connects a server.</summary>
internal sealed class QualificationAccountSetup(IClientCatalog catalog, IAccountSetup roles) : IAccountSetup
{
    internal Failure Credentials
    {
        get; set;
    }
    internal Failure Incoming
    {
        get; set;
    }
    internal Failure Outgoing
    {
        get; set;
    }

    public async Task<AccountSetupResult> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool valid = request.Incoming.Port is > 0 and <= 65535 && request.Outgoing.Port is > 0 and <= 65535 &&
            Enum.IsDefined(request.Incoming.Tls) && Enum.IsDefined(request.Outgoing.Tls);
        if (!valid)
        {
            return new AccountSetupResult(Failure.Validation, Failure.Validation, Failure.Validation, []);
        }

        ImmutableArray<MailboxSnapshot> mailboxes = Incoming == Failure.None ?
            await catalog.MailboxesAsync(cancellationToken) : [];
        return new AccountSetupResult(
            Credentials,
            Incoming,
            Outgoing,
            mailboxes.IsDefaultOrEmpty ? [] : mailboxes.RemoveAll(mailbox => mailbox.Account != new AccountId(1))
        );
    }

    public Task<Failure> SetRolesAsync(AccountId account, FolderRoles selected, CancellationToken cancellationToken)
    {
        return roles.SetRolesAsync(account, selected, cancellationToken);
    }

    public Task<Failure> RestoreCredentialsAsync(
        AccountId account,
        string incomingPassword,
        string outgoingPassword,
        bool sessionOnly,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Failure.PolicyBlocked);
    }
}

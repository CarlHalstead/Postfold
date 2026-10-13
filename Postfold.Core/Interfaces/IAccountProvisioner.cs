using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Composes account connections and exposes explicit credential and mailbox-role configuration.</summary>
public interface IAccountProvisioner
{
    bool NetworkAllowed
    {
        get;
    }
    IClientCatalog Catalog
    {
        get;
    }

    /// <summary>Returns an unused account id, including ids retained for removed accounts.</summary>
    Task<AccountId> NextAccountIdAsync(CancellationToken cancellationToken);

    Task<Failure> TestOutgoingConnectionAsync(
        AccountId account,
        MailEndpoint outgoing,
        ICredentialStore credentials,
        CancellationToken cancellationToken
    );

    Task<Outcome<IMailAccountEngine>> ConnectAccountAsync(
        AccountId account,
        string displayName,
        MailEndpoint incoming,
        MailEndpoint outgoing,
        ICredentialStore credentials,
        string? selectedDraftMailbox,
        CancellationToken cancellationToken
    );

    Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken);

    Task<Failure> ReplaceCredentialsAsync(
        AccountId account,
        ICredentialStore credentials,
        CancellationToken cancellationToken
    );
}

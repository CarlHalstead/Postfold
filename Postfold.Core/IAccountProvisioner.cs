using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

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
    Task<Outcome<IMailAccountEngine>> ConnectAccountAsync(AccountId account, string displayName,
        MailEndpoint incoming, MailEndpoint outgoing, ICredentialStore credentials, string? selectedDraftMailbox,
        CancellationToken cancellationToken);
    Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken);
    Task<Failure> ReplaceCredentialsAsync(AccountId account, ICredentialStore credentials,
        CancellationToken cancellationToken);
}

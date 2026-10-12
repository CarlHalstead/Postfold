using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IAccountSetup
{
    Task<AccountSetupResult> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken);
    Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken);
    Task<Failure> RestoreCredentialsAsync(AccountId account, string incomingPassword, string outgoingPassword,
        bool sessionOnly, CancellationToken cancellationToken);
}

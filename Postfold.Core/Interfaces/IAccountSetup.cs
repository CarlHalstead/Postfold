using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Coordinates explicit account setup, mailbox-role selection and credential restoration for presentation.</summary>
public interface IAccountSetup
{
    /// <summary>Returns optional server suggestions; lookup misses and network failures are not setup errors.</summary>
    Task<AccountServerSettings> DiscoverAsync(string email, CancellationToken cancellationToken);

    Task<AccountSetupResult> AddAsync(AccountSetupRequest request, CancellationToken cancellationToken);

    Task<Failure> SetRolesAsync(AccountId account, FolderRoles roles, CancellationToken cancellationToken);

    Task<Failure> RestoreCredentialsAsync(
        AccountId account,
        string incomingPassword,
        string outgoingPassword,
        CancellationToken cancellationToken
    );
}

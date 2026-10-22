using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Explicit provider connection and local token lifecycle. Background mail never opens a browser.</summary>
public interface IProviderAccounts
{
    /// <summary>Reports registration/policy availability without acquiring tokens or opening a browser.</summary>
    ImmutableArray<ProviderAvailability> Providers { get; }

    /// <summary>Connects only in response to explicit user intent and retains any account saved before initial-sync failure.</summary>
    Task<ProviderConnectionResult> ConnectProviderAsync(ProviderSetupRequest request, CancellationToken cancellationToken);

    /// <summary>Revalidates the established identity without replacing its mailbox or replaying held work.</summary>
    Task<ProviderConnectionResult> ReconnectProviderAsync(AccountId account, CancellationToken cancellationToken);

    /// <summary>Drains owners and removes local token bytes, retaining binding, cached mail and protected work.</summary>
    Task<Failure> DeleteProviderTokensAsync(AccountId account, CancellationToken cancellationToken);

    /// <summary>Returns Unsupported when the provider has no implemented server grant revocation capability.</summary>
    Task<Failure> RevokeProviderAsync(AccountId account, CancellationToken cancellationToken);
}

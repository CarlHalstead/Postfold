using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Protocols.Services;

/// <summary>Adapts existing secure password references to connection-time credential resolution.</summary>
public sealed class PasswordTransportCredentials(ICredentialStore store) : ITransportCredentials
{
    public Task<Outcome<Credential>> ResolveAsync(AccountId account, MailEndpoint endpoint, CancellationToken cancellationToken)
    {
        return store.ReadAsync(endpoint.CredentialKey, cancellationToken);
    }
}

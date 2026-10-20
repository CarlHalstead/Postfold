using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>One owned authenticator per account. Silent acquisition never opens a browser or changes mail state.</summary>
public interface IAccountAuthenticator
{
    /// <summary>Acquires a resource-bound token; interaction is permitted only for an explicit connect/reconnect.</summary>
    Task<Outcome<AccessToken>> AcquireAsync(bool interactive, CancellationToken cancellationToken);

    /// <summary>Deletes local credentials only. The host must stop account work first; retained mail is untouched.</summary>
    Task<Outcome<bool>> DeleteCredentialsAsync(CancellationToken cancellationToken);
}

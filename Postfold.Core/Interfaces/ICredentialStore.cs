using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reads and writes credentials through an explicit secure or session-only store; failures do not imply plaintext fallback.</summary>
public interface ICredentialStore
{
    Task<Outcome<Credential>> ReadAsync(string key, CancellationToken cancellationToken);

    Task<Outcome<bool>> WriteAsync(string key, Credential credential, CancellationToken cancellationToken);
}

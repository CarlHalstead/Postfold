using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Stores;

public sealed class CombinedCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _secure;
    private readonly ICredentialStore _session;

    public CombinedCredentialStore(ICredentialStore secure, ICredentialStore session)
    {
        _secure = secure;
        _session = session;
    }

    public async Task<Outcome<Credential>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        Outcome<Credential> session = await _session.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return session.IsSuccess ? session : await _secure.ReadAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public Task<Outcome<bool>> WriteAsync(string key, Credential credential, CancellationToken cancellationToken)
    {
        return _secure.WriteAsync(key, credential, cancellationToken);
    }
}

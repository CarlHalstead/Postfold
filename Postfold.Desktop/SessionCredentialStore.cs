using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Desktop;

/// <summary>Construct only after an explicit caller choice to keep credentials for this session.</summary>
public sealed class SessionCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, Credential> _credentials = new ConcurrentDictionary<string, Credential>();

    public Task<Outcome<Credential>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_credentials.TryGetValue(key, out Credential? credential) ?
            Outcome<Credential>.Success(credential) : Outcome<Credential>.Fail(Failure.NotFound));
    }

    public Task<Outcome<bool>> WriteAsync(string key, Credential credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _credentials[key] = credential;
        return Task.FromResult(Outcome<bool>.Success(true));
    }
}

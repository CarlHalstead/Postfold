using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Fakes;

/// <summary>Stores synthetic credentials for isolated protocol and account fixtures.</summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
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

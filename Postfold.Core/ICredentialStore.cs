using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ICredentialStore
{
    Task<Outcome<Credential>> ReadAsync(string key, CancellationToken cancellationToken);
    Task<Outcome<bool>> WriteAsync(string key, Credential credential, CancellationToken cancellationToken);
}

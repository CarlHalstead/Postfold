using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMessageContentReader
{
    Task<Outcome<Stream>> OpenAsync(ProviderIdentity identity, CancellationToken cancellationToken);
    Task<Outcome<Stream>> OpenBodyAsync(ProviderIdentity identity, CancellationToken cancellationToken);
}

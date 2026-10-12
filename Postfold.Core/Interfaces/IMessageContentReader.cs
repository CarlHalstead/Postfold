using Postfold.Core.Models;
using Postfold.Core.Models.Messages;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Opens provider MIME content by provider identity; successful streams must be disposed by the caller.</summary>
public interface IMessageContentReader
{
    Task<Outcome<Stream>> OpenAsync(ProviderIdentity identity, CancellationToken cancellationToken);

    Task<Outcome<Stream>> OpenBodyAsync(ProviderIdentity identity, CancellationToken cancellationToken);
}

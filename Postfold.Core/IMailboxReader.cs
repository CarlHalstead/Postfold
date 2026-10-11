using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMailboxReader : IAsyncDisposable
{
    AccountId Account
    {
        get;
    }
    Capabilities Capabilities
    {
        get;
    }
    Task<Outcome<ImmutableArray<MailboxSnapshot>>> DiscoverAsync(CancellationToken cancellationToken);
    Task<Outcome<MetadataPage>> ReadAsync(string scope, Checkpoint? checkpoint, int limit,
        CancellationToken cancellationToken);
}

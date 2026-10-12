using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMimeDraftReader
{
    Task<Outcome<ComposeContent>> ReadComposeAsync(AccountId account, Stream source, CancellationToken cancellationToken);
}

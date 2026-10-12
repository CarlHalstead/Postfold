using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMimeDraftWriter
{
    Task<Outcome<bool>> WriteDraftAsync(ComposeContent content, Stream destination, CancellationToken cancellationToken);
}

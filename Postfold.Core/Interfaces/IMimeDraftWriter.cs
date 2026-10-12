using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Serialises editable draft content without treating it as a frozen submission.</summary>
public interface IMimeDraftWriter
{
    Task<Outcome<bool>> WriteDraftAsync(ComposeContent content, Stream destination, CancellationToken cancellationToken);
}

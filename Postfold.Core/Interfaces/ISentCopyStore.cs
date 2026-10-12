using Postfold.Core.Enums;
using Postfold.Core.Models.Outbox;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Stores an already-submitted message's sent copy without submitting the message again.</summary>
public interface ISentCopyStore
{
    Task<Failure> SaveAsync(FrozenSubmission submission, Stream content, CancellationToken cancellationToken);
}

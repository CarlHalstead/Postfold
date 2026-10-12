using Postfold.Core.Models.Outbox;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Submits frozen MIME and envelope data while preserving acknowledgement and recipient evidence.</summary>
public interface IMessageSubmitter
{
    Task<SubmissionOutcome> SubmitAsync(FrozenSubmission submission, Stream mime, CancellationToken cancellationToken);
}

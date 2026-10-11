using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMessageSubmitter
{
    Task<SubmissionOutcome> SubmitAsync(FrozenSubmission submission, Stream mime, CancellationToken cancellationToken);
}

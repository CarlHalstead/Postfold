using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface ISentCopyStore
{
    Task<Failure> SaveAsync(FrozenSubmission submission, Stream content, CancellationToken cancellationToken);
}

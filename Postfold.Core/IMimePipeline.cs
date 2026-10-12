using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public interface IMimePipeline
{
    Task<Outcome<MimeSnapshot>> ParseAsync(Stream source, CancellationToken cancellationToken);
    Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(Stream source, CancellationToken cancellationToken);
    Task<Outcome<bool>> CopyAttachmentAsync(Stream source, string part, Stream destination,
        CancellationToken cancellationToken);
    Task<Outcome<bool>> FreezeAsync(ComposeContent content, Stream destination, CancellationToken cancellationToken);
}

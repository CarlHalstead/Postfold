using Postfold.Core.Models;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mime;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Shares bounded MIME parsing, selected-part extraction and submission freezing across provider adapters.</summary>
public interface IMimePipeline
{
    Task<Outcome<MimeSnapshot>> ParseAsync(Stream source, CancellationToken cancellationToken);

    Task<Outcome<MimeHtmlDocument>> ReadHtmlAsync(Stream source, CancellationToken cancellationToken);

    Task<Outcome<bool>> CopyAttachmentAsync(
        Stream source,
        string part,
        Stream destination,
        CancellationToken cancellationToken
    );

    Task<Outcome<bool>> FreezeAsync(ComposeContent content, Stream destination, CancellationToken cancellationToken);
}

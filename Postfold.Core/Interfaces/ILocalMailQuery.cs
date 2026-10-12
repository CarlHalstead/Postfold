using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Search;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Queries locally cached mail and content availability without fetching missing content from the provider.</summary>
public interface ILocalMailQuery
{
    Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);

    Task<Outcome<BlobReference>> ReadBodyAsync(AccountId account, MessageId message, CancellationToken cancellationToken);

    Task<ImmutableArray<AttachmentSnapshot>> ReadAttachmentsAsync(AccountId account, MessageId message, CancellationToken cancellationToken);
}

using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Reconstructs editable compose content from account-owned draft MIME and attachment blobs.</summary>
public interface IMimeDraftReader
{
    Task<Outcome<ComposeContent>> ReadComposeAsync(AccountId account, Stream source, CancellationToken cancellationToken);
}

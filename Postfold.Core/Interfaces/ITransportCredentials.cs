using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Resolves credentials immediately before a transport connection. Never permits interactive authentication or writes passwords.</summary>
public interface ITransportCredentials
{
    /// <summary>Returns a credential valid for the requested account and configured endpoint, or a safe typed failure.</summary>
    Task<Outcome<Credential>> ResolveAsync(AccountId account, MailEndpoint endpoint, CancellationToken cancellationToken);
}

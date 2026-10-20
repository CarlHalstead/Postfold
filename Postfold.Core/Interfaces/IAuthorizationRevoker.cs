using Postfold.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Optional provider grant revocation. Distinct from account deactivation and local cache deletion.</summary>
public interface IAuthorizationRevoker
{
    Task<Outcome<bool>> RevokeAsync(CancellationToken cancellationToken);
}

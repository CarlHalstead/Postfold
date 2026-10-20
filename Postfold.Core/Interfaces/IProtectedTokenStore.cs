using Postfold.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Bounded encrypted binary storage, separate from password credentials. No plaintext/session fallback.</summary>
public interface IProtectedTokenStore
{
    Task<Outcome<byte[]>> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Atomically replaces one account cache; failure preserves the preceding committed bytes.</summary>
    Task<Outcome<bool>> WriteAsync(string key, byte[] bytes, CancellationToken cancellationToken);

    Task<Outcome<bool>> DeleteAsync(string key, CancellationToken cancellationToken);
}

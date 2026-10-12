using Postfold.Core.Enums;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Represents cancellable account work whose expected failure is reported to its scheduler.</summary>
public interface IAccountWork
{
    Task<Failure> RunAsync(CancellationToken cancellationToken);
}

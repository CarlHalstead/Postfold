using Postfold.Core.Enums;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Interfaces;

/// <summary>Ready is Failure.None only after durable drafts and all owned mail resources are closed.</summary>
internal interface IUpdateShutdown
{
    Task<Failure> PrepareUpdateShutdownAsync(CancellationToken cancellationToken);
}

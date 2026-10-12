using Postfold.Core.Enums;
using Postfold.Core.Models.Operations;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core.Interfaces;

/// <summary>Applies a persisted mutation intent to a provider while reporting expected failures as typed evidence.</summary>
public interface IMailboxMutator
{
    Task<Failure> ApplyAsync(MutationIntent intent, CancellationToken cancellationToken);
}

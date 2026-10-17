using Postfold.Core.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Interfaces;

/// <summary>Owns explicit session shutdown and the independent authenticated native handoff.</summary>
internal interface IUpdateRestart
{
    Task<UpdateStatus> RestartAsync(VerifiedUpdateSource release, CancellationToken cancellationToken);
}

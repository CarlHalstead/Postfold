using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Postfold.Storage.Migrations;

namespace Postfold.Engine.Tests.Fixtures.Storage;

[DbContext(typeof(EvolutionContext))]
[Migration("20261023214228_DraftDeletionIntent")]
internal sealed class DraftDeletionEvolution : DraftDeletionIntent
{
}

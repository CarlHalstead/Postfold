using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace Postfold.Engine.Tests.Fixtures.Storage;

[DbContext(typeof(EvolutionContext))]
[Migration("20261023220000_AddEvolutionMetadata")]
internal sealed class AddEvolutionMetadata : Migration
{
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.Sql("ALTER TABLE Accounts ADD COLUMN FutureMetadata TEXT; PRAGMA user_version=4;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new InvalidOperationException("Synthetic migration downgrades are prohibited.");
    }
}

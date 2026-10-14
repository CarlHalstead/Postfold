using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Postfold.Storage.Migrations;
using System;

namespace Postfold.Engine.Tests.Fixtures.Storage;

[DbContext(typeof(EvolutionContext))]
[Migration("20261014150754_InitialEfProfile")]
internal sealed class InitialEvolution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (MigrationOperation operation in new InitialEfProfile().UpOperations)
        {
            migrationBuilder.Operations.Add(operation);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new InvalidOperationException("Synthetic migration downgrades are prohibited.");
    }
}

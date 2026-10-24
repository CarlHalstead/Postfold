using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Postfold.Storage.Migrations;

/// <inheritdoc />
public partial class DraftDeletionIntent : Migration
{
    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.AddColumn<long>(
            name: "DeletePending",
            table: "Drafts",
            type: "INTEGER",
            nullable: false,
            defaultValueSql: "0"
        ).Annotation("Relational:ColumnOrder", 11);
        migrationBuilder.Sql("PRAGMA user_version=3;");
    }

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    )
    {
        migrationBuilder.DropColumn(
            name: "DeletePending",
            table: "Drafts"
        );
        migrationBuilder.Sql("PRAGMA user_version=2;");
    }
}

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System;

namespace Postfold.Engine.Tests.Fixtures.Storage;

[DbContext(typeof(EvolutionContext))]
[Migration("20261014160100_RebuildEvolutionMemberships")]
internal sealed class RebuildEvolutionMemberships : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE Memberships_new(
                AccountId INTEGER NOT NULL, MessageId INTEGER NOT NULL, FolderId INTEGER NOT NULL,
                SeenGeneration TEXT NOT NULL CHECK(length(SeenGeneration)>=0),
                PRIMARY KEY(AccountId,MessageId,FolderId),
                FOREIGN KEY(AccountId,MessageId) REFERENCES Messages(AccountId,MessageId),
                FOREIGN KEY(AccountId,FolderId) REFERENCES Collections(AccountId,FolderId));
            INSERT INTO Memberships_new(AccountId,MessageId,FolderId,SeenGeneration)
                SELECT AccountId,MessageId,FolderId,SeenGeneration FROM Memberships;
            DROP TABLE Memberships;
            ALTER TABLE Memberships_new RENAME TO Memberships;
            CREATE INDEX FolderMessages ON Memberships(AccountId,FolderId,MessageId);
            PRAGMA user_version=4;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new InvalidOperationException("Synthetic migration downgrades are prohibited.");
    }
}

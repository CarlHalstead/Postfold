using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Engine.Tests.Fixtures.Storage;
using Postfold.Storage.Contexts;
using Postfold.Storage.Models;
using Postfold.Storage.Services;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class DraftDeletionMigrationTests
{
    [Test]
    public async Task OpenAsync_WhenFormatTwoContainsProtectedDraft_UpgradesWithoutChangingItsContentOrVersions()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        BlobStore blobs = new BlobStore(Path.Combine(directory, "objects"));
        using MemoryStream content = new MemoryStream(Encoding.UTF8.GetBytes("Existing protected draft"));
        BlobReference blob = await blobs.WriteAsync(EngineFixture.Account, content, 1048576, true, CancellationToken.None);
        Guid id = Guid.NewGuid();
        using (SqliteConnection connection = ProfileMigration.Connect(database, readOnly: false))
        {
            using ProfileDbContext context = new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite(connection).Options);
            ProfileEvidence.Execute(connection, context.GetService<IMigrator>().GenerateScript(toMigration: ProfileMigrationManifest.Versions[0].Id));
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Accounts(AccountId,ProviderKind,DisplayName) VALUES(1,'Synthetic','Existing account'); " +
                "INSERT INTO Blobs VALUES($b,1,$n,$l,$h,1,0); " +
                "INSERT INTO Drafts VALUES($d,1,NULL,1,0,0,$b,NULL,NULL,0,0); " +
                "INSERT INTO DraftVersions VALUES($d,1,'local:1',$b,'Local',0);";
            insert.Parameters.AddWithValue("$b", blob.Id.ToString("N"));
            insert.Parameters.AddWithValue("$n", blob.Id.ToString("N") + ".blob");
            insert.Parameters.AddWithValue("$l", blob.Length);
            insert.Parameters.AddWithValue("$h", blob.Sha256);
            insert.Parameters.AddWithValue("$d", id.ToString("N"));
            insert.ExecuteNonQuery();
        }

        // Act
        await using SqliteProfile upgraded = (await SqliteProfile.OpenAsync(database, TimeProvider.System, CancellationToken.None)).Value!;
        DraftSnapshot saved = (await upgraded.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;

        // Assert
        Assert.That(saved.Content, Is.EqualTo(blob));
        Assert.That(saved.DeletePending, Is.False);
        Assert.That(saved.VersionCount, Is.EqualTo(1));
        Assert.That(await upgraded.DraftVersionsAsync(saved.Account, id, CancellationToken.None), Has.Length.EqualTo(1));
        Assert.That(upgraded.AdmittedMigration, Is.EqualTo("20261023214228_DraftDeletionIntent"));
        Assert.That(Directory.GetDirectories(directory, "*.before-migration-*"), Has.Length.EqualTo(1));
        Assert.That(
            ProfileMigrationPlan.Create(connection => new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>().UseSqlite(connection).Options)).Versions,
            Is.EqualTo(ProfileMigrationManifest.Versions)
        );
    }
}

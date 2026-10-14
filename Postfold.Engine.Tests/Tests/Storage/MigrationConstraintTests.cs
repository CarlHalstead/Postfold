using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Engine.Tests.Fixtures.Storage;
using Postfold.Storage.Services;
using System.IO;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Tests.Storage;

public sealed class MigrationConstraintTests
{
    [TestCase("UPDATE Drafts SET AccountId=3 WHERE AccountId=1;")]
    [TestCase("UPDATE Drafts SET IdentityId=20 WHERE AccountId=1;")]
    [TestCase("UPDATE Collections SET ParentId=(SELECT FolderId FROM Collections WHERE AccountId=2) WHERE AccountId=1;")]
    [TestCase("UPDATE Bodies SET BlobId=(SELECT BlobId FROM Blobs WHERE AccountId=2 LIMIT 1) WHERE AccountId=1;")]
    [TestCase("UPDATE Attachments SET AccountId=2;")]
    [TestCase("UPDATE Memberships SET AccountId=3;")]
    [TestCase("UPDATE ImapInstances SET MessageId=(SELECT MessageId FROM Messages WHERE AccountId=2 LIMIT 1) WHERE AccountId=1;")]
    [TestCase("UPDATE OutboxSubmissions SET SourceDraftId=(SELECT DraftId FROM Drafts WHERE AccountId=2 LIMIT 1) WHERE AccountId=1;")]
    [TestCase("UPDATE OutboxSubmissions SET Predecessor=(SELECT SubmissionId FROM OutboxSubmissions WHERE AccountId=2 LIMIT 1) WHERE AccountId=1;")]
    [TestCase("INSERT INTO Identities VALUES(11,1,'duplicate@synthetic.invalid','Duplicate','signature',1);")]
    [TestCase("DELETE FROM Accounts WHERE AccountId=1;")]
    public async Task Execute_WhenAccountConstraintIsViolated_RejectsWithoutChangingAnyRow(string sql)
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        string database = Path.Combine(directory, "profile.db");
        await MigrationTests.PopulateAsync(directory);
        string original = ProfileEvidence.Rows(database);
        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: false);

        // Act
        SqliteException? failure = Assert.Throws<SqliteException>(() => ProfileEvidence.Execute(connection, sql));

        // Assert
        Assert.That(failure!.SqliteErrorCode, Is.EqualTo(19));
        Assert.That(ProfileEvidence.Rows(database), Is.EqualTo(original));
        Assert.That(ProfileMigration.Scalar(connection, "PRAGMA foreign_key_check;"), Is.Null);
    }
}

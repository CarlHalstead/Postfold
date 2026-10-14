using Microsoft.Data.Sqlite;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Messages;
using Postfold.Storage.Models;
using Postfold.Storage.Stores;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Postfold.Storage.Services;

internal static class ProfileBackup
{
    internal static string Create(
        SqliteConnection source,
        string database,
        ProfileMigrationVersion version,
        CancellationToken cancellationToken
    )
    {
        string directory = database + ".before-migration-" + Guid.NewGuid().ToString("N");
        BlobStore.VerifyPath(directory);
        Directory.CreateDirectory(Path.Combine(directory, "objects"));
        string copy = Path.Combine(directory, "profile.db");
        Dictionary<Guid, BlobReference> references = new Dictionary<Guid, BlobReference>();
        using (SqliteConnection destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = copy,
            Pooling = false,
            ForeignKeys = true
        }.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
            ProfileMigration.CheckIntegrity(destination);
            using SqliteCommand command = destination.CreateCommand();
            command.CommandText = "SELECT BlobId,AccountId,ObjectName,Length,Hash,Protected FROM Blobs;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                Guid id = Guid.ParseExact(reader.GetString(0), "N");
                if (reader.GetString(2) != id.ToString("N") + ".blob")
                {
                    throw new InvalidDataException("Invalid backup object name.");
                }

                references.Add(id, new BlobReference(
                    id,
                    new AccountId(reader.GetInt64(1)),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetBoolean(5)
                ));
            }
        }

        List<ProfileBackupFile> files = new List<ProfileBackupFile> { Inspect(copy, "profile.db") };
        string objects = Path.Combine(Path.GetDirectoryName(database)!, "objects");
        BlobStore.VerifyPath(objects);
        HashSet<Guid> copied = new HashSet<Guid>();
        foreach (string ownerPath in Directory.Exists(objects) ? Directory.EnumerateFiles(objects, "*.owner") : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            BlobStore.VerifyPath(ownerPath);
            Guid id = Guid.ParseExact(Path.GetFileNameWithoutExtension(ownerPath), "N");
            BlobOwnership owner = JsonSerializer.Deserialize<BlobOwnership>(File.ReadAllText(ownerPath)) ??
                throw new InvalidDataException("Missing backup object ownership.");
            if (owner.Account.Value <= 0)
            {
                throw new InvalidDataException("Invalid backup account ownership.");
            }

            bool referenced = references.TryGetValue(id, out BlobReference? reference);
            if (!owner.Protected && !referenced)
            {
                continue;
            }

            if (reference is not null && (owner.Account != reference.Account || owner.Protected != reference.Protected))
            {
                throw new InvalidDataException("Backup object ownership mismatch.");
            }

            int contents = 0;
            foreach (string extension in new[] { ".blob", ".tmp" })
            {
                string content = Path.Combine(objects, id.ToString("N") + extension);
                if (!File.Exists(content))
                {
                    continue;
                }

                ProfileBackupFile evidence = Inspect(content, "objects/" + Path.GetFileName(content));
                if (extension == ".blob" && reference is not null &&
                    (reference.Length != evidence.Length || reference.Sha256 != evidence.Hash))
                {
                    throw new InvalidDataException("Backup object failed reference validation.");
                }

                Copy(content, Path.Combine(directory, evidence.Name), cancellationToken);
                files.Add(evidence);
                contents++;
            }

            if (contents == 0 || (referenced && !File.Exists(Path.Combine(objects, id.ToString("N") + ".blob"))))
            {
                throw new InvalidDataException("Protected backup content is missing.");
            }

            string name = "objects/" + Path.GetFileName(ownerPath);
            files.Add(Inspect(ownerPath, name));
            Copy(ownerPath, Path.Combine(directory, name), cancellationToken);
            copied.Add(id);
        }

        if (references.Keys.Any(id => !copied.Contains(id)))
        {
            throw new InvalidDataException("Referenced backup ownership is missing.");
        }

        ProfileBackupManifest manifest = new ProfileBackupManifest(1, Path.GetFileName(directory), DateTimeOffset.UtcNow, version.Id, version.Format, version.SchemaHash, files);
        string manifestPath = Path.Combine(directory, "manifest.json");
        ProfileMigration.WriteEvidence(manifestPath, manifest);
        ValidateFiles(directory, manifest);
        ProfileMigration.WriteEvidence(Path.Combine(directory, "complete.json"), Inspect(manifestPath, "manifest.json"));
        Validate(directory, version);
        return Path.GetFileName(directory);
    }

    internal static void Validate(string directory, ProfileMigrationVersion? expected = null)
    {
        BlobStore.VerifyPath(directory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        ProfileBackupFile receipt = JsonSerializer.Deserialize<ProfileBackupFile>(
            File.ReadAllText(Path.Combine(directory, "complete.json"))
        ) ?? throw new InvalidDataException("Incomplete migration backup.");
        if (receipt != Inspect(manifestPath, "manifest.json"))
        {
            throw new InvalidDataException("Migration backup manifest failed validation.");
        }

        ProfileBackupManifest manifest = JsonSerializer.Deserialize<ProfileBackupManifest>(File.ReadAllText(manifestPath)) ??
            throw new InvalidDataException("Missing migration backup manifest.");
        if (manifest.Version != 1 || manifest.Id != Path.GetFileName(directory) || (expected is not null &&
            (manifest.SourceMigration != expected.Id || manifest.Format != expected.Format || manifest.SchemaHash != expected.SchemaHash)))
        {
            throw new InvalidDataException("Migration backup version mismatch.");
        }

        ValidateFiles(directory, manifest);
        using SqliteConnection connection = ProfileMigration.Connect(Path.Combine(directory, "profile.db"), readOnly: true);
        ProfileMigration.CheckIntegrity(connection);
        if (ProfileMigration.SchemaHash(connection) != manifest.SchemaHash ||
            Convert.ToInt32(ProfileMigration.Scalar(connection, "PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture) != manifest.Format ||
            !Equals(ProfileMigration.Scalar(connection, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1;"), manifest.SourceMigration))
        {
            throw new InvalidDataException("Migration backup schema mismatch.");
        }
    }

    private static void ValidateFiles(string directory, ProfileBackupManifest manifest)
    {
        HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProfileBackupFile file in manifest.Files)
        {
            string name = file.Name;
            bool validObject = name.StartsWith("objects/", StringComparison.Ordinal) && name.Length > 40 &&
                Guid.TryParseExact(name[8..40], "N", out _) && name[40..] is ".blob" or ".tmp" or ".owner";
            if ((!validObject && name != "profile.db") || !names.Add(name) || Inspect(Path.Combine(directory, name), name) != file)
            {
                throw new InvalidDataException("Migration backup file failed validation.");
            }
        }

        if (!names.Contains("profile.db"))
        {
            throw new InvalidDataException("Migration backup database is missing.");
        }
    }

    private static ProfileBackupFile Inspect(string path, string name)
    {
        BlobStore.VerifyPath(path);
        using FileStream stream = File.OpenRead(path);
        return new ProfileBackupFile(name, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Copy(string source, string target, CancellationToken cancellationToken)
    {
        BlobStore.VerifyPath(source);
        BlobStore.VerifyPath(target);
        using FileStream input = File.OpenRead(source);
        using FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[65536];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, count);
        }

        output.Flush(flushToDisk: true);
    }
}

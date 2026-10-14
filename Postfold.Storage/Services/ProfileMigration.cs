using Microsoft.Data.Sqlite;
using Postfold.Core.Enums;
using Postfold.Storage.Models;
using Postfold.Storage.Stores;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Storage.Services;

internal static class ProfileMigration
{
    private static readonly ProfileMigrationPlan DefaultPlan = new ProfileMigrationPlan(
        ProfileMigrationExecutor.ApplyAsync,
        ProfileMigrationManifest.Versions
    );

    /// <summary>The returned exclusive handle belongs to the admitted profile's entire lifetime.</summary>
    internal static Task<FileStream> AdmitAsync(string path, CancellationToken cancellationToken)
    {
        return AdmitAsync(path, null, cancellationToken);
    }

    internal static async Task<FileStream> AdmitAsync(
        string path,
        ProfileMigrationPlan? plan,
        CancellationToken cancellationToken
    )
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            BlobStore.VerifyPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BlobStore.VerifyPath(path + ".lease");
            FileStream lease = new FileStream(path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                plan ??= DefaultPlan;
                plan.Checkpoint?.Invoke("lease-acquired");
                await AdmitOwnedAsync(path, plan, cancellationToken).ConfigureAwait(false);
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static SqliteConnection Connect(string path, bool readOnly)
    {
        BlobStore.VerifyPath(path);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            BlobStore.VerifyPath(path + suffix);
        }
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    internal static void CheckIntegrity(SqliteConnection connection)
    {
        if (!Equals(Scalar(connection, "PRAGMA quick_check;"), "ok") || Scalar(connection, "PRAGMA foreign_key_check;") is not null)
        {
            throw new ProfileAdmissionException(Failure.Corrupt);
        }
    }

    internal static string SchemaHash(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT type,name,tbl_name,sql FROM sqlite_master
            WHERE name NOT GLOB 'sqlite_*'
            AND NOT (type='table' AND name IN ('__EFMigrationsHistory','__EFMigrationsLock'))
            ORDER BY type,name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder schema = new StringBuilder();
        while (reader.Read())
        {
            for (int field = 0; field < 4; field++)
            {
                string value = reader.IsDBNull(field) ? "" : reader.GetString(field);
                // EF emits platform-specific SQL newlines; retain the reviewed Windows fingerprint on every OS.
                schema.Append(field == 3 ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : value);
                schema.Append('\0');
            }

            schema.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema.ToString())));
    }

    internal static void WriteEvidence<TValue>(string path, TValue value)
    {
        BlobStore.VerifyPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
        using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static async Task AdmitOwnedAsync(string path, ProfileMigrationPlan plan, CancellationToken cancellationToken)
    {
        string receiptPath = path + ".migration.json";
        BlobStore.VerifyPath(receiptPath);
        ProfileMigrationReceipt? receipt = File.Exists(receiptPath)
            ? JsonSerializer.Deserialize<ProfileMigrationReceipt>(File.ReadAllText(receiptPath))
            : null;
        ProfileMigrationVersion latest = plan.Versions[^1];
        if (receipt is not null && !plan.Versions.Any(version => version.Id == receipt.Target))
        {
            throw new ProfileAdmissionException(Failure.NewerSchema);
        }

        int applied = 0;
        if (File.Exists(path))
        {
            using SqliteConnection read = Connect(path, readOnly: true);
            int format = Convert.ToInt32(Scalar(read, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (format > latest.Format)
            {
                throw new ProfileAdmissionException(Failure.NewerSchema);
            }

            await CheckReadOnlyIntegrityAsync(path, cancellationToken).ConfigureAwait(false);
            bool history = Equals(Scalar(read, "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory' AND type='table';"), 1L);
            if (history)
            {
                ValidateTable(read, "__EFMigrationsHistory", "MigrationId|TEXT|1|1;ProductVersion|TEXT|1|0;");
                using SqliteCommand command = read.CreateCommand();
                command.CommandText = "SELECT MigrationId,ProductVersion FROM __EFMigrationsHistory ORDER BY MigrationId;";
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    if (!plan.Versions.Any(version => version.Id == id))
                    {
                        throw new ProfileAdmissionException(Failure.NewerSchema);
                    }

                    if (applied >= plan.Versions.Count || plan.Versions[applied].Id != id || string.IsNullOrWhiteSpace(reader.GetString(1)))
                    {
                        throw new ProfileAdmissionException(Failure.NeedsReview);
                    }

                    applied++;
                }
            }

            if (applied == 0)
            {
                if (receipt is null || receipt.Complete || receipt.Backup is not null || format != 0)
                {
                    throw new ProfileAdmissionException(Failure.NeedsReview);
                }

                ValidateInfrastructure(read, receipt is { Complete: false });
            }
            else
            {
                ProfileMigrationVersion current = plan.Versions[applied - 1];
                if (format != current.Format || SchemaHash(read) != current.SchemaHash)
                {
                    throw new ProfileAdmissionException(Failure.NeedsReview);
                }
            }

            ValidateLock(read, receipt is { Complete: false });
        }
        else
        {
            if (receipt is not null && (receipt.Complete || receipt.Backup is not null))
            {
                throw new ProfileAdmissionException(Failure.NeedsReview);
            }

            string directory = Path.GetDirectoryName(path)!;
            if (Directory.EnumerateFileSystemEntries(directory).Any(entry =>
                entry != path + ".lease" && entry != receiptPath && !entry.StartsWith(receiptPath + ".", StringComparison.Ordinal)))
            {
                throw new ProfileAdmissionException(Failure.NeedsReview);
            }
        }

        if (receipt is not null && !receipt.Complete && receipt.Backup is not null)
        {
            string backup = receipt.Backup;
            if (Path.GetFileName(backup) != backup || !backup.StartsWith(Path.GetFileName(path) + ".before-migration-", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Invalid migration backup identity.");
            }

            ProfileBackup.Validate(Path.Combine(Path.GetDirectoryName(path)!, backup));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (applied < plan.Versions.Count)
        {
            string? backup = null;
            if (applied > 0)
            {
                using SqliteConnection source = Connect(path, readOnly: true);
                backup = ProfileBackup.Create(source, path, plan.Versions[applied - 1], cancellationToken);
                plan.Checkpoint?.Invoke("backup-complete");
            }

            WriteEvidence(receiptPath, new ProfileMigrationReceipt(latest.Id, backup, false));
            plan.Checkpoint?.Invoke("receipt-prepared");
            using SqliteConnection connection = Connect(path, readOnly: false);
            RecoverLock(connection, path, receipt is { Complete: false });
            await ApplyMigrationsAsync(connection, plan, latest.Id, cancellationToken).ConfigureAwait(false);

            CheckIntegrity(connection);
            if (Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != latest.Format ||
                SchemaHash(connection) != latest.SchemaHash)
            {
                throw new ProfileAdmissionException(Failure.NeedsReview);
            }

            plan.Checkpoint?.Invoke("migrations-committed");
            WriteEvidence(receiptPath, new ProfileMigrationReceipt(latest.Id, backup, true));
            plan.Checkpoint?.Invoke("receipt-complete");
        }
        else if (receipt is not null && !receipt.Complete)
        {
            WriteEvidence(receiptPath, receipt with
            {
                Complete = true
            });
        }

        using SqliteConnection admitted = Connect(path, readOnly: false);
        cancellationToken.ThrowIfCancellationRequested();
        RecoverLock(admitted, path, receipt is { Complete: false });
        Scalar(admitted, "PRAGMA journal_mode=WAL;");
    }

    private static async Task CheckReadOnlyIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        // The lifetime lease excludes admitted writers. These independent checks use two owned connections,
        // plus the classification connection: at most three startup readers, below the normal four-reader bound.
        Task quick = Task.Run(() =>
        {
            using SqliteConnection connection = Connect(path, readOnly: true);
            if (!Equals(Scalar(connection, "PRAGMA quick_check;"), "ok"))
            {
                throw new ProfileAdmissionException(Failure.Corrupt);
            }
        }, cancellationToken);
        Task foreignKeys = Task.Run(() =>
        {
            using SqliteConnection connection = Connect(path, readOnly: true);
            if (Scalar(connection, "PRAGMA foreign_key_check;") is not null)
            {
                throw new ProfileAdmissionException(Failure.Corrupt);
            }
        }, cancellationToken);
        await Task.WhenAll(quick, foreignKeys).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        ProfileMigrationPlan plan,
        string target,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await plan.ApplyAsync(connection, target, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProfileAdmissionException(Failure.Unavailable);
        }
    }

    private static void ValidateInfrastructure(SqliteConnection connection, bool allowAbandoned)
    {
        if (!Equals(Scalar(connection, """
            SELECT count(*) FROM sqlite_master WHERE name NOT GLOB 'sqlite_*'
            AND NOT (type='table' AND name IN ('__EFMigrationsHistory','__EFMigrationsLock'));
            """), 0L))
        {
            throw new ProfileAdmissionException(Failure.NeedsReview);
        }

        ValidateLock(connection, allowAbandoned);
    }

    private static void ValidateLock(SqliteConnection connection, bool allowAbandoned = false)
    {
        if (!Equals(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsLock';"), 1L))
        {
            return;
        }

        ValidateTable(connection, "__EFMigrationsLock", "Id|INTEGER|1|1;Timestamp|TEXT|1|0;");
        long count = Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM __EFMigrationsLock;"), CultureInfo.InvariantCulture);
        if (count != 0 && (!allowAbandoned || count != 1 ||
            !Equals(Scalar(connection, "SELECT count(*) FROM __EFMigrationsLock WHERE Id=1 AND length(Timestamp)>0;"), 1L)))
        {
            throw new ProfileAdmissionException(Failure.NeedsReview);
        }
    }

    private static void RecoverLock(SqliteConnection connection, string path, bool allowAbandoned)
    {
        ValidateLock(connection, allowAbandoned);
        if (allowAbandoned && Equals(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsLock';"), 1L) &&
            Equals(Scalar(connection, "SELECT count(*) FROM __EFMigrationsLock;"), 1L))
        {
            // The lifetime lease, validated history/schema and incomplete durable receipt establish abandonment.
            // Timestamp age never permits stealing, and unexplained locks remain held for review.
            WriteEvidence(path + ".abandoned-lock-" + Guid.NewGuid().ToString("N") + ".json",
                Convert.ToString(Scalar(connection, "SELECT Timestamp FROM __EFMigrationsLock WHERE Id=1;"), CultureInfo.InvariantCulture));
            Scalar(connection, "DELETE FROM __EFMigrationsLock WHERE Id=1;");
        }
    }

    private static void ValidateTable(SqliteConnection connection, string table, string expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"" + table + "\");";
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder shape = new StringBuilder();
        while (reader.Read())
        {
            shape.Append(reader.GetString(1)).Append('|').Append(reader.GetString(2)).Append('|')
                .Append(reader.GetInt64(3)).Append('|').Append(reader.GetInt64(5)).Append(';');
        }

        if (shape.ToString() != expected)
        {
            throw new ProfileAdmissionException(Failure.NeedsReview);
        }
    }
}

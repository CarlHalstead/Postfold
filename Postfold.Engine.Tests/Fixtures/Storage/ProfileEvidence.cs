using Microsoft.Data.Sqlite;
using Postfold.Storage.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Postfold.Engine.Tests.Fixtures.Storage;

internal static class ProfileEvidence
{
    internal static readonly string[] Tables = ["Accounts", "Identities", "Collections", "Messages", "Memberships", "ImapInstances",
        "Blobs", "Bodies", "Attachments", "SyncState", "PendingOperations", "Drafts", "DraftVersions", "OutboxSubmissions",
        "SubmissionRecipients", "Preferences", "SearchText"];

    internal static string Rows(string database)
    {
        using SqliteConnection connection = ProfileMigration.Connect(database, readOnly: true);
        List<string> result = new List<string>();
        foreach (string table in Tables)
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(\"" + table + "\");";
            List<string> names = new List<string>();
            using (SqliteDataReader reader = columns.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetString(1) != "FutureMetadata")
                    {
                        names.Add("\"" + reader.GetString(1) + "\"");
                    }
                }
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT " + string.Join(',', names) + " FROM \"" + table + "\";";
            using SqliteDataReader rows = command.ExecuteReader();
            List<string> values = new List<string>();
            while (rows.Read())
            {
                string?[] fields = Enumerable.Range(0, rows.FieldCount)
                    .Select(index => rows.IsDBNull(index) ? null : Convert.ToString(rows.GetValue(index), CultureInfo.InvariantCulture))
                    .ToArray();
                values.Add(JsonSerializer.Serialize(fields));
            }

            values.Sort(StringComparer.Ordinal);
            result.Add(table + ":" + string.Join('\n', values));
        }

        return string.Join('\n', result);
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

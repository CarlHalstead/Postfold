using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Postfold.Storage.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Storage.Models;

internal sealed record ProfileMigrationPlan(Func<SqliteConnection, string, CancellationToken, Task> ApplyAsync, IReadOnlyList<ProfileMigrationVersion> Versions)
{
    internal Action<string>? Checkpoint { get; init; }

    /// <summary>Builds guards from the shipped reviewed migration chain, never from an occupied profile.</summary>
    internal static ProfileMigrationPlan Create(Func<SqliteConnection, DbContext> createContext)
    {
        using SqliteConnection connection = ProfileMigration.Connect(":memory:", readOnly: false);
        using DbContext context = createContext(connection);
        List<ProfileMigrationVersion> versions = new List<ProfileMigrationVersion>();
        foreach (string id in context.Database.GetMigrations())
        {
            context.GetService<IMigrator>().Migrate(id);
            int format = Convert.ToInt32(ProfileMigration.Scalar(connection, "PRAGMA user_version;"));
            if (format != versions.Count + 2)
            {
                throw new InvalidOperationException("Each reviewed migration must advance the profile format guard.");
            }

            versions.Add(new ProfileMigrationVersion(id, format, ProfileMigration.SchemaHash(connection)));
        }

        if (versions.Count == 0)
        {
            throw new InvalidOperationException("The profile migration chain is empty.");
        }

        return new ProfileMigrationPlan(async (source, target, cancellationToken) =>
        {
            using DbContext migratorContext = createContext(source);
            await migratorContext.GetService<IMigrator>().MigrateAsync(target, cancellationToken).ConfigureAwait(false);
        }, versions);
    }
}

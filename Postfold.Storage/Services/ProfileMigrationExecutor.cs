using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Postfold.Storage.Contexts;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Storage.Services;

internal static class ProfileMigrationExecutor
{
    // Keep EF types out of ordinary profile admission's fields and method signatures.
    // Existing profiles validate the reviewed manifest without loading/building the EF execution stack.
    internal static async Task ApplyAsync(SqliteConnection connection, string target, CancellationToken cancellationToken)
    {
        using ProfileDbContext context = new ProfileDbContext(new DbContextOptionsBuilder<ProfileDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.GetService<IMigrator>().MigrateAsync(target, cancellationToken).ConfigureAwait(false);
    }
}

using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Fixtures.Storage;

internal sealed class MigrationBarrierInterceptor(bool enabled) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (enabled)
        {
            if (command.CommandText.Contains("CREATE TABLE \"Accounts\"", System.StringComparison.Ordinal))
            {
                MigrationProcess.Signal("before-initial-ddl");
            }

            if (command.CommandText.Contains("PRAGMA user_version=2", System.StringComparison.Ordinal))
            {
                MigrationProcess.Signal("before-format-guard");
            }

            if (command.CommandText.Contains("CREATE TABLE Memberships_new", System.StringComparison.Ordinal))
            {
                MigrationProcess.Signal("before-rebuild");
            }
        }

        return new ValueTask<InterceptionResult<int>>(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        if (enabled && command.CommandText.Contains("INSERT INTO \"__EFMigrationsHistory\"", System.StringComparison.Ordinal))
        {
            MigrationProcess.Signal("history-in-transaction");
        }

        return new ValueTask<int>(result);
    }
}

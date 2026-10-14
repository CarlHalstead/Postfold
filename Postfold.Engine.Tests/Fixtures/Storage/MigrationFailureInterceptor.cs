using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Engine.Tests.Fixtures.Storage;

internal sealed class MigrationFailureInterceptor(bool diskFull) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        if (command.CommandText.Contains("ALTER TABLE Accounts ADD COLUMN", StringComparison.Ordinal))
        {
            command.CommandText = diskFull
                ? "UPDATE Accounts SET Configuration=zeroblob(10485760);"
                : "INSERT INTO Accounts(AccountId,ProviderKind,DisplayName) VALUES(0,'Synthetic','Constraint failure');";
        }

        return new ValueTask<InterceptionResult<int>>(result);
    }
}

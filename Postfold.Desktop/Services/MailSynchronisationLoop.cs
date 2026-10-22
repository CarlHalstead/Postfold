using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

/// <summary>
/// Polls retained accounts at startup and thirty seconds after each completed pass. Passes never
/// overlap. The desktop session owns the returned task and cancels/drains it before profile shutdown;
/// this loop acquires credentials silently and never processes the outbox or requeues held sends.
/// </summary>
internal sealed class MailSynchronisationLoop(
    IMailApplication application,
    TimeProvider time
)
{
    internal async Task RunAsync(
        Func<AccountId, Failure, CancellationToken, Task> refreshed,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    foreach (AccountConfiguration account in await application.Catalog.AccountsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Failure result;
                        try
                        {
                            result = await application.SynchroniseAsync(account.Id, cancellationToken).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                            result = Failure.Transient;
                        }
                        await refreshed(account.Id, result, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    // A temporarily unavailable local catalog must not terminate future refreshes.
                }
                await Task.Delay(TimeSpan.FromSeconds(30), time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

using Postfold.UpdateBroker.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UpdateBroker;

internal static class Program
{
    private static async Task<int> Main()
    {
        using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return await NativeUpdateBroker.RunAsync(Console.OpenStandardInput(), Console.Out, deadline.Token).ConfigureAwait(false);
    }
}

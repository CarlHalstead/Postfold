using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Stores;
using System;
using System.IO;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

internal static class UpdateTrustProcess
{
    internal static void Run(string[] args)
    {
        if (args is not [string path, string target, string kind] ||
            !Path.GetFullPath(path).Contains(Path.Combine("artifacts", "engine-tests"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Disposable trust fixture required.");
        }
        string root = File.ReadAllText(path + ".public");
        using UpdateTrustStore store = UpdateTrustStore.Open(path, root, "stable").Value!;
        store.Checkpoint = checkpoint => Pause(checkpoint, target);
        Outcome<UpdateTrustState> accepted = store.Accept(File.ReadAllBytes(path + ".document"), File.ReadAllBytes(path + ".signature"), kind == "transition");
        if (!accepted.IsSuccess)
        {
            throw new InvalidOperationException("Synthetic signed input refused.");
        }
        Pause("accepted", target);
    }

    private static void Pause(string checkpoint, string target)
    {
        if (checkpoint == target)
        {
            Console.WriteLine(checkpoint);
            Console.WriteLine(Environment.ProcessId);
            Console.Out.Flush();
            Console.ReadLine();
        }
    }
}

using Postfold.UpdateSigner.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.UpdateSigner;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "verify")
        {
            using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            bool verified = await OfflineUpdateSigner.VerifyAsync(args[1], args[2], args[3], args[4], deadline.Token);
            Console.WriteLine(verified ? "Exact signed manifest, feed and package verified." : "Verification refused.");
            return verified ? 0 : 1;
        }
        if (args.Length != 5 || args[0] is not ("manifest" or "transition") || Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Usage: Postfold.UpdateSigner manifest|transition document artifacts-directory encrypted-pkcs8 signature-output");
            Console.Error.WriteLine("Verify: Postfold.UpdateSigner verify document artifacts-directory public-spki-base64 signature");
            Console.Error.WriteLine("The passphrase is read privately from an interactive terminal; no key generation or publication occurs.");
            return 2;
        }
        char[] password = new char[1024];
        int length = 0;
        Console.Write("Offline key passphrase: ");
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (length > 0)
                {
                    password[--length] = '\0';
                }
            }
            else if (!char.IsControl(key.KeyChar) && length < password.Length)
            {
                password[length++] = key.KeyChar;
            }
        }
        Console.WriteLine();
        try
        {
            bool signed = await OfflineUpdateSigner.SignAsync(args[1], args[2], args[3], args[4], password.AsMemory(0, length), args[0] == "transition", CancellationToken.None);
            Console.WriteLine(signed ? "Exact-byte signature written; publication requires separate approval." : "Signing refused; inspect contract, exact artifacts, key identity and output path.");
            return signed ? 0 : 1;
        }
        finally
        {
            Array.Clear(password);
        }
    }
}

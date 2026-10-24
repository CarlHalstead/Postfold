using Postfold.UpdateBroker.Models;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Postfold.UpdateBroker.Services;

/// <summary>
/// Retains exact executable copies outside the installation before releasing its current directory.
/// Content-addressed copies survive the old app's exit and may be reused only after locked verification.
/// </summary>
public sealed class StagedNativeUpdate : IDisposable
{
    private readonly LockedUpdateFiles _locks;

    private StagedNativeUpdate(LockedUpdateFiles locks, string broker, NativeUpdateRequest request)
    {
        _locks = locks;
        Broker = broker;
        Request = request;
    }

    /// <summary>The locked executable copy that survives the installation's process-stop boundary.</summary>
    public string Broker { get; }
    /// <summary>The original trusted request with only its helper path redirected to an exact locked copy.</summary>
    public NativeUpdateRequest Request { get; }

    public void Dispose()
    {
        _locks.Dispose();
    }

    /// <summary>Verifies the installed executables, stages exact copies and releases all current-directory guards.</summary>
    public static StagedNativeUpdate Prepare(string broker, NativeUpdateRequest request)
    {
        string installation = LockedUpdateFiles.CanonicalPath(request.InstallRoot);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation.ToUpperInvariant())));
        using LockedUpdateFiles sources = new LockedUpdateFiles();
        using FileStream sourceBroker = sources.Verify(broker, request.BrokerSha256);
        using FileStream sourceHelper = sources.Verify(request.HelperPath, request.HelperSha256, 33554432);
        // Short routing keys keep native paths bounded; every reused file still requires its full digest.
        string directory = Path.Combine(Path.GetDirectoryName(installation)!, "Postfold.UpdateRuntime", identity[..16], request.BrokerSha256[..16] + "-" + request.HelperSha256[..16]);
        LockedUpdateFiles staged = new LockedUpdateFiles();
        try
        {
            Directory.CreateDirectory(directory);
            staged.PinDirectory(directory);
            staged.AllowChildRenames(directory);
            string stagedBroker = Copy(sourceBroker, directory, "Postfold.UpdateBroker.exe", request.BrokerSha256, staged);
            string stagedHelper = Copy(sourceHelper, directory, "Update.exe", request.HelperSha256, staged);
            return new StagedNativeUpdate(staged, stagedBroker, request with { HelperPath = stagedHelper });
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    internal void PreventInheritance()
    {
        _locks.PreventInheritance();
    }

    private static string Copy(FileStream source, string directory, string name, string digest, LockedUpdateFiles guards)
    {
        string destination = Path.Combine(directory, name);
        if (!File.Exists(destination))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        using FileStream verified = guards.Verify(destination, digest, source.Length);
        return destination;
    }
}

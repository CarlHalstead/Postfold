using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Stores;

/// <summary>Current-user DPAPI token files with account/key entropy, private ACLs and bounded atomic replacement.</summary>
public sealed class WindowsTokenStore : IProtectedTokenStore
{
    public const int MaximumBytes = 1048576;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public WindowsTokenStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public Task<Outcome<byte[]>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => OperatingSystem.IsWindows() ? ReadWindows(key) :
            Outcome<byte[]>.Fail(Failure.CredentialStorageUnavailable), cancellationToken);
    }

    public Task<Outcome<bool>> WriteAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
        {
            return Task.FromResult(Outcome<bool>.Fail(Failure.Validation));
        }

        return ExecuteAsync(() => OperatingSystem.IsWindows() ? WriteWindows(key, bytes, cancellationToken) :
            Outcome<bool>.Fail(Failure.CredentialStorageUnavailable), cancellationToken);
    }

    public Task<Outcome<bool>> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        return ExecuteAsync(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return Outcome<bool>.Fail(Failure.CredentialStorageUnavailable);
            }

            string path = TokenPath(key);
            ValidateExisting(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return Outcome<bool>.Success(true);
        }, cancellationToken);
    }

    private async Task<Outcome<TValue>> ExecuteAsync<TValue>(Func<Outcome<TValue>> operation, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Outcome<TValue>.Fail(Failure.CredentialStorageUnavailable);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (CryptographicException)
        {
            return Outcome<TValue>.Fail(Failure.Corrupt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Outcome<TValue>.Fail(Failure.CredentialStorageUnavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private Outcome<byte[]> ReadWindows(string key)
    {
        string path = TokenPath(key);
        ValidateExisting(path);
        if (!File.Exists(path))
        {
            return Outcome<byte[]>.Fail(Failure.NotFound);
        }

        using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0 || input.Length > MaximumBytes + 4096)
        {
            return Outcome<byte[]>.Fail(Failure.Corrupt);
        }

        byte[] encrypted = new byte[checked((int)input.Length)];
        input.ReadExactly(encrypted);
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy(key), DataProtectionScope.CurrentUser);
        if (plain.Length == 0 || plain.Length > MaximumBytes)
        {
            CryptographicOperations.ZeroMemory(plain);
            return Outcome<byte[]>.Fail(Failure.Corrupt);
        }

        return Outcome<byte[]>.Success(plain);
    }

    [SupportedOSPlatform("windows")]
    private Outcome<bool> WriteWindows(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        string path = TokenPath(key);
        ValidateExisting(path);
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
        if (!Directory.Exists(_root))
        {
            DirectorySecurity directoryAcl = new DirectorySecurity();
            directoryAcl.SetOwner(owner);
            directoryAcl.SetAccessRuleProtection(true, false);
            directoryAcl.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            ));
            FileSystemAclExtensions.CreateDirectory(directoryAcl, _root);
        }

        ValidateExisting(path);
        byte[] encrypted = ProtectedData.Protect(bytes, Entropy(key), DataProtectionScope.CurrentUser);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough
            ))
            {
                FileSecurity acl = new FileSecurity();
                acl.SetOwner(owner);
                acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(temporary).SetAccessControl(acl);
                WindowsCredentialStore.ValidateAcl(new FileInfo(temporary).GetAccessControl());
                output.Write(encrypted);
                output.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            return Outcome<bool>.Success(true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    [SupportedOSPlatform("windows")]
    private void ValidateExisting(string path)
    {
        WindowsCredentialStore.CheckPath(path);
        if (Directory.Exists(_root))
        {
            WindowsCredentialStore.ValidateAcl(new DirectoryInfo(_root).GetAccessControl());
        }

        if (File.Exists(path))
        {
            WindowsCredentialStore.ValidateAcl(new FileInfo(path).GetAccessControl());
        }
    }

    private string TokenPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".token");
    }

    private static byte[] Entropy(string key)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("Postfold token v1:" + key));
    }
}

using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Stores;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    public WindowsCredentialStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public async Task<Outcome<Credential>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Outcome<Credential>.Fail(Failure.Unavailable);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OperatingSystem.IsWindows() ? ReadWindows(key) :
                Outcome<Credential>.Fail(Failure.Unavailable), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or JsonException)
        {
            return Outcome<Credential>.Fail(Failure.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Outcome<bool>> WriteAsync(string key, Credential credential, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Outcome<bool>.Fail(Failure.Unavailable);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => OperatingSystem.IsWindows() ? WriteWindows(key, credential, cancellationToken) :
                Outcome<bool>.Fail(Failure.Unavailable), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Outcome<bool>.Fail(Failure.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    [SupportedOSPlatform("windows")]
    private Outcome<Credential> ReadWindows(string key)
    {
        string path = SecretPath(key);
        if (!File.Exists(path))
        {
            return Outcome<Credential>.Fail(Failure.NotFound);
        }

        CheckPath(path);
        ValidateAcl(new FileInfo(path).GetAccessControl());
        if (new FileInfo(path).Length > 65536)
        {
            return Outcome<Credential>.Fail(Failure.Corrupt);
        }

        byte[] encrypted = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy(key), DataProtectionScope.CurrentUser);
        try
        {
            Credential? credential = JsonSerializer.Deserialize<Credential>(plain);
            return credential is null ? Outcome<Credential>.Fail(Failure.Corrupt) :
                Outcome<Credential>.Success(credential);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    [SupportedOSPlatform("windows")]
    private Outcome<bool> WriteWindows(string key, Credential credential, CancellationToken cancellationToken)
    {
        if (credential.Secret.Length > 16384)
        {
            return Outcome<bool>.Fail(Failure.Validation);
        }

        CheckPath(_root);
        DirectoryInfo directory = Directory.CreateDirectory(_root);
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ??
            throw new UnauthorizedAccessException("Current user identity unavailable.");
        DirectorySecurity acl = new DirectorySecurity();
        acl.SetOwner(owner);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow
        ));
        directory.SetAccessControl(acl);
        ValidateAcl(directory.GetAccessControl());
        string path = SecretPath(key);
        CheckPath(path);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(credential);
        byte[] encrypted;
        try
        {
            encrypted = ProtectedData.Protect(plain, Entropy(key), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (FileStream target = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough
        ))
        {
            FileSecurity fileAcl = new FileSecurity();
            fileAcl.SetOwner(owner);
            fileAcl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileAcl.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temporary).SetAccessControl(fileAcl);
            target.Write(encrypted);
            target.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, overwrite: true);
        ValidateAcl(new FileInfo(path).GetAccessControl());
        return Outcome<bool>.Success(true);
    }

    private string SecretPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".secret");
    }

    private static byte[] Entropy(string key)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes("Postfold credential v1:" + key));
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateAcl(FileSystemSecurity security)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
        if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("Credential permissions are not restricted.");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !owner.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException("Credential permissions admit another identity.");
            }
        }
    }

    internal static void CheckPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Credential paths cannot contain filesystem redirection.");
            }

            current = Path.GetDirectoryName(current);
        }
    }
}

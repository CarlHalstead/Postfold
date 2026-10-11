using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Desktop;

namespace Postfold.Engine.Tests;

public sealed class CredentialTests
{
    [Test]
    public async Task WriteAsync_WhenUsingWindowsDpapi_ProtectsSyntheticSecretAndRestrictsAcl()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Current-user DPAPI and Windows ACL qualification runs on Windows only.");
            return;
        }

        string path = EngineFixture.DirectoryPath();
        WindowsCredentialStore store = new WindowsCredentialStore(path);
        Credential secret = new Credential("synthetic-secret-for-native-test", false);

        // Act
        Outcome<bool> written = await store.WriteAsync("synthetic-key", secret, CancellationToken.None);
        Outcome<Credential> read = await store.ReadAsync("synthetic-key", CancellationToken.None);
        string file = Directory.GetFiles(path, "*.secret")[0];
        byte[] bytes = await File.ReadAllBytesAsync(file);
        FileSecurity security = new FileInfo(file).GetAccessControl();

        // Assert
        Assert.That(written.IsSuccess, Is.True);
        Assert.That(read.Value!.Secret, Is.EqualTo(secret.Secret));
        Assert.That(Encoding.UTF8.GetString(bytes), Does.Not.Contain(secret.Secret));
        Assert.That(secret.ToString(), Does.Not.Contain(secret.Secret));
        Assert.That(security.AreAccessRulesProtected, Is.True);
        Assert.That(security.GetOwner(typeof(SecurityIdentifier)), Is.EqualTo(WindowsIdentity.GetCurrent().User));
        Assert.Throws<CryptographicException>(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                ProtectedData.Unprotect(bytes, new byte[32], DataProtectionScope.CurrentUser);
            }
        });
    }

    [Test]
    public async Task ReadAsync_WhenPermissionsAdmitAnotherIdentity_RefusesProtectedSecret()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows ACL qualification runs on Windows only.");
            return;
        }

        string path = EngineFixture.DirectoryPath();
        WindowsCredentialStore store = new WindowsCredentialStore(path);
        await store.WriteAsync("synthetic", new Credential("synthetic", true), CancellationToken.None);
        FileInfo file = new FileInfo(Directory.GetFiles(path, "*.secret")[0]);
        FileSecurity acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(acl);

        // Act
        Outcome<Credential> result = await store.ReadAsync("synthetic", CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(result.Value, Is.Null);
    }

    [Test]
    public async Task ReadAsync_WhenPlatformHasNoNativeStore_ReportsUnavailableWithoutFallback()
    {
        // Arrange
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("This capability test covers non-Windows hosts; Windows runs the real DPAPI tests.");
        }

        WindowsCredentialStore store = new WindowsCredentialStore(EngineFixture.DirectoryPath());

        // Act
        Outcome<bool> result = await store.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Unavailable));
    }
}

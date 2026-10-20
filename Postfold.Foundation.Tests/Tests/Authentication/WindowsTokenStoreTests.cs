using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

[TestFixture]
[Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class WindowsTokenStoreTests
{
    [Test]
    public async Task WriteAsync_WhenRotated_EncryptsLargeCacheAndIsolatesAccount()
    {
        // Arrange
        string root = Path.Combine(EngineFixture.DirectoryPath(), "tokens");
        WindowsTokenStore store = new WindowsTokenStore(root);
        byte[] first = Encoding.UTF8.GetBytes(new string('x', 100000));
        await store.WriteAsync("one", first, CancellationToken.None);
        await store.WriteAsync("two", [2, 3], CancellationToken.None);

        // Act
        Outcome<bool> rotated = await store.WriteAsync("one", [4, 5, 6], CancellationToken.None);
        Outcome<byte[]> actual = await new WindowsTokenStore(root).ReadAsync("one", CancellationToken.None);
        Outcome<byte[]> other = await store.ReadAsync("two", CancellationToken.None);

        // Assert
        Assert.That(rotated.IsSuccess, Is.True);
        Assert.That(actual.Value, Is.EqualTo(new byte[] { 4, 5, 6 }));
        Assert.That(other.Value, Is.EqualTo(new byte[] { 2, 3 }));
        Assert.That(Directory.GetFiles(root, "*.tmp"), Is.Empty);
        foreach (string file in Directory.GetFiles(root))
        {
            Assert.That(File.ReadAllBytes(file), Is.Not.EqualTo(actual.Value));
            WindowsCredentialStore.ValidateAcl(new FileInfo(file).GetAccessControl());
        }

        WindowsCredentialStore.ValidateAcl(new DirectoryInfo(root).GetAccessControl());
    }

    [Test]
    public async Task WriteAsync_WhenLockedOversizedOrCanceled_PreservesCommittedBytes()
    {
        // Arrange
        string root = Path.Combine(EngineFixture.DirectoryPath(), "tokens");
        WindowsTokenStore store = new WindowsTokenStore(root);
        await store.WriteAsync("one", [1, 2], CancellationToken.None);
        string file = Directory.GetFiles(root).Single();
        using CancellationTokenSource canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        Outcome<bool> locked;
        using (FileStream held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            locked = await store.WriteAsync("one", [3, 4], CancellationToken.None);
        }

        Outcome<bool> oversized = await store.WriteAsync("one", new byte[WindowsTokenStore.MaximumBytes + 1], CancellationToken.None);

        // Assert
        Assert.That(locked.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
        Assert.That(oversized.Failure, Is.EqualTo(Failure.Validation));
        await Assert.ThatAsync(async () => await store.WriteAsync("one", [5], canceled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That((await store.ReadAsync("one", CancellationToken.None)).Value, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(Directory.GetFiles(root, "*.tmp"), Is.Empty);
    }

    [Test]
    public async Task ReadAsync_WhenCorruptOversizedOrCopiedToAnotherKey_FailsClosed()
    {
        // Arrange
        string root = Path.Combine(EngineFixture.DirectoryPath(), "tokens");
        WindowsTokenStore store = new WindowsTokenStore(root);
        await store.WriteAsync("one", [1, 2], CancellationToken.None);
        string first = Directory.GetFiles(root).Single();
        byte[] encrypted = File.ReadAllBytes(first);
        await store.WriteAsync("two", [3, 4], CancellationToken.None);
        string second = Directory.GetFiles(root).Single(file => file != first);
        File.WriteAllBytes(second, encrypted);

        // Act
        Outcome<byte[]> copied = await store.ReadAsync("two", CancellationToken.None);
        File.WriteAllBytes(first, [0, 1, 2, 3]);
        Outcome<byte[]> corrupt = await store.ReadAsync("one", CancellationToken.None);
        File.WriteAllBytes(first, new byte[WindowsTokenStore.MaximumBytes + 4097]);
        Outcome<byte[]> oversized = await store.ReadAsync("one", CancellationToken.None);

        // Assert
        Assert.That(copied.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(corrupt.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(oversized.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(File.Exists(first), Is.True);
    }

    [Test]
    public async Task ReadAsync_WhenAclAdmitsAnotherIdentity_RefusesReadWriteAndDelete()
    {
        // Arrange
        string root = Path.Combine(EngineFixture.DirectoryPath(), "tokens");
        WindowsTokenStore store = new WindowsTokenStore(root);
        await store.WriteAsync("one", [1], CancellationToken.None);
        string file = Directory.GetFiles(root).Single();
        FileSecurity original = new FileInfo(file).GetAccessControl();
        FileSecurity insecure = new FileInfo(file).GetAccessControl();
        insecure.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(insecure);
        try
        {
            // Act
            Outcome<byte[]> read = await store.ReadAsync("one", CancellationToken.None);
            Outcome<bool> write = await store.WriteAsync("one", [2], CancellationToken.None);
            Outcome<bool> delete = await store.DeleteAsync("one", CancellationToken.None);

            // Assert
            Assert.That(read.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
            Assert.That(write.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
            Assert.That(delete.Failure, Is.EqualTo(Failure.CredentialStorageUnavailable));
            Assert.That(File.Exists(file), Is.True);
        }
        finally
        {
            new FileInfo(file).SetAccessControl(original);
        }
    }

    [Test]
    public async Task DeleteAsync_WhenRequested_LeavesOtherAccountIntact()
    {
        // Arrange
        WindowsTokenStore store = new WindowsTokenStore(Path.Combine(EngineFixture.DirectoryPath(), "tokens"));
        await store.WriteAsync("one", [1], CancellationToken.None);
        await store.WriteAsync("two", [2], CancellationToken.None);

        // Act
        Outcome<bool> removed = await store.DeleteAsync("one", CancellationToken.None);

        // Assert
        Assert.That(removed.IsSuccess, Is.True);
        Assert.That((await store.ReadAsync("one", CancellationToken.None)).Failure, Is.EqualTo(Failure.NotFound));
        Assert.That((await store.ReadAsync("two", CancellationToken.None)).Value, Is.EqualTo(new byte[] { 2 }));
    }
}

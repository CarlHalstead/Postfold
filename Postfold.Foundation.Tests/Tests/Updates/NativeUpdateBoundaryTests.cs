using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.UpdateBroker.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class NativeUpdateBoundaryTests
{
    [Test]
    [Platform("Win")]
    public void PreventInheritance_WhenNormalChildWillStart_RetainsLocksWithoutTransferringTheirLifetime()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "Postfold-U05-inheritance-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [1, 2, 3]);
        using LockedUpdateFiles locks = new LockedUpdateFiles();
        using FileStream pinned = locks.OpenRead(path);
        Assert.That(GetHandleInformation(pinned.SafeFileHandle, out uint before), Is.True);
        Assert.That(before & 1, Is.EqualTo(1));

        // Act
        locks.PreventInheritance();

        // Assert
        Assert.That(GetHandleInformation(pinned.SafeFileHandle, out uint after), Is.True);
        Assert.That(after & 1, Is.Zero);
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, [4, 5, 6]));
    }

    [Test]
    public void Open_WhenNativeTrustIsMissing_DoesNotInitializeAReplacementJournal()
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        // macOS temporary paths can contain symlink ancestors that the trust store rejects.
        string directory = EngineFixture.DirectoryPath();
        string path = Path.Combine(directory, "trust.journal");

        // Act
        Failure failure = UpdateTrustStore.Open(path, fixture.PublicKey, "stable", allowCreate: false).Failure;

        // Assert
        Assert.That(failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(File.Exists(path), Is.False);
        Assert.That(File.Exists(path + ".head"), Is.False);
    }

    [Test]
    [Platform("Win")]
    public void AllowChildRenames_WhenDestinationParentIsNeeded_KeepsIdentityAndPinnedNonemptyChild()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "Postfold-U04-parent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using LockedUpdateFiles locks = new LockedUpdateFiles();
        locks.PinDirectory(path);

        // Act
        locks.AllowChildRenames(path);
        using SafeFileHandle writer = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

        // Assert
        Assert.That(writer.IsInvalid, Is.False);
        string sentinel = Directory.GetFiles(path, ".postfold-apply-*.guard")[0];
        Assert.Throws<IOException>(() => File.Delete(sentinel));
        Assert.Throws<IOException>(() => Directory.Move(path, path + "-substituted"));
    }

    [Test]
    [Platform("Win")]
    public void PinDirectory_WhenHeld_PreventsNativeWriteHandlesAndEmptyDirectoryRename()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "Postfold-U04-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using LockedUpdateFiles locks = new LockedUpdateFiles();

        // Act
        locks.PinDirectory(path);
        using SafeFileHandle writer = CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        int error = Marshal.GetLastWin32Error();

        // Assert
        Assert.That(writer.IsInvalid, Is.True);
        Assert.That(error, Is.EqualTo(32), "A reparse/write handle must fail with a sharing violation.");
        Assert.Throws<IOException>(() => Directory.Move(path, path + "-substituted"));
    }

    [Test]
    [Platform("Win")]
    public void OpenRead_WhenHeld_PreventsFileAndAncestorSubstitutionButAllowsNativeReads()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "Postfold-U04-lock-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "cache");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "package.nupkg");
        File.WriteAllBytes(path, [1, 2, 3]);
        using LockedUpdateFiles locks = new LockedUpdateFiles();

        // Act
        using FileStream pinned = locks.OpenRead(path);
        byte[] actual = File.ReadAllBytes(path);

        // Assert
        Assert.That(actual, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, [4, 5, 6]));
        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-replaced"));
        Assert.Throws<IOException>(() => Directory.Move(root, root + "-replaced"));
    }

    [Test]
    [Platform("Win")]
    public void Verify_WhenExecutableDigestDiffers_RefusesBeforeExecution()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "Postfold-U04-tamper-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, [1, 2, 3]);
        using LockedUpdateFiles locks = new LockedUpdateFiles();

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => locks.Verify(path, Convert.ToHexString(SHA256.HashData(new byte[] { 3, 2, 1 }))));
    }

    [TestCase("C:\\cache\\..\\package")]
    [TestCase("C:\\cache\\package:stream")]
    [TestCase("C:\\cache.\\package")]
    [TestCase("\\\\server\\share\\package")]
    [TestCase("C:/cache/package")]
    [Platform("Win")]
    public void OpenRead_WhenPathIsUnsafe_Refuses(string path)
    {
        // Arrange
        using LockedUpdateFiles locks = new LockedUpdateFiles();

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => locks.OpenRead(path));
    }

    [TestCase(0)]
    [TestCase(524289)]
    [Platform("Win")]
    public async Task RunAsync_WhenInputIsUnbounded_RefusesWithoutAnInstaller(int length)
    {
        // Arrange
        using MemoryStream input = new MemoryStream(BitConverter.GetBytes(length));
        using StringWriter events = new StringWriter();

        // Act
        int exit = await NativeUpdateBroker.RunAsync(input, events, CancellationToken.None);

        // Assert
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(events.ToString(), Does.StartWith("refused:"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);
}

using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Services.Updates;
using Postfold.Desktop.Stores;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateBundleTests
{
    [TestCase(CompressionLevel.NoCompression)]
    [TestCase(CompressionLevel.Optimal)]
    public async Task DownloadFullAsync_WhenUnusedInstallerIsCorrupt_StagesOnlyTheSignedPackage(CompressionLevel compression)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        byte[] bundle = FixtureUpdateTransport.Bundle(Entries(transport), compression);
        int position = 0;
        while (BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(position)) == 0x04034b50)
        {
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bundle.AsSpan(position + 26));
            int extra = BinaryPrimitives.ReadUInt16LittleEndian(bundle.AsSpan(position + 28));
            int data = position + 30 + nameLength + extra;
            if (Encoding.ASCII.GetString(bundle, position + 30, nameLength) == "Postfold-win-x64-stable-Setup.exe")
            {
                bundle[data] ^= 255;
                break;
            }
            position = checked(data + (int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(position + 18)));
        }
        transport.BundleBytes = bundle;
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "zip-auxiliary-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        VerifiedUpdateSource selected = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;

        // Act
        Outcome<string> result = await selected.DownloadFullAsync(Path.Combine(cache, selected.Manifest.Package.Name), CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(File.ReadAllBytes(result.Value!), Is.EqualTo(fixture.Package));
        Assert.That(Directory.GetFiles(cache).Select(Path.GetFileName), Is.EquivalentTo(new[] { "staging.lock", selected.Manifest.Package.Name }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DownloadFullAsync_WhenTransferOrVerificationCannotContinue_RemovesOwnedScratchAndRefusesPromotion(bool permission)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "zip-io-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        VerifiedUpdateSource selected = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        if (!permission)
        {
            transport.DuringDownload = () => throw new IOException("Synthetic write failure.");
        }
        Action? verifying = permission ? () => throw new UnauthorizedAccessException("Synthetic access refusal.") : null;
        string target = Path.Combine(cache, selected.Manifest.Package.Name);

        // Act
        Outcome<string> result = await selected.DownloadFullAsync(target, verifying, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetFiles(cache, "*.partial"), Is.Empty);
    }

    [TestCase("product")]
    [TestCase("version")]
    [TestCase("xml")]
    [TestCase("missing")]
    public async Task DownloadFullAsync_WhenSignedPackageHasInvalidNativeIdentity_RefusesBeforePromotion(string fault)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        string xml = fault == "xml" ? "<!DOCTYPE package SYSTEM 'file:///forbidden'><package/>" :
            "<package><metadata><id>" + (fault == "product" ? "Other" : "Postfold") + "</id><version>" + (fault == "version" ? "1.0.0" : "1.1.0") + "</version></metadata></package>";
        byte[] package = FixtureUpdateTransport.Bundle(new Dictionary<string, byte[]> { [fault == "missing" ? "README.txt" : "Postfold.nuspec"] = Encoding.UTF8.GetBytes(xml) });
        JsonNode feed = JsonNode.Parse(fixture.Feed)!;
        feed["Assets"]![0]!["Size"] = package.Length;
        feed["Assets"]![0]!["SHA256"] = UpdateFixture.Digest(package);
        byte[] feedBytes = Encoding.UTF8.GetBytes(feed.ToJsonString());
        byte[] document = fixture.Manifest(change: manifest =>
        {
            manifest["package"]!["length"] = package.Length;
            manifest["package"]!["sha256"] = UpdateFixture.Digest(package);
            manifest["feed"]!["length"] = feedBytes.Length;
            manifest["feed"]!["sha256"] = UpdateFixture.Digest(feedBytes);
        });
        transport.Files["Postfold.win-x64-stable.manifest.json"] = document;
        transport.Files["Postfold.win-x64-stable.manifest.sig"] = fixture.Sign(document);
        transport.Files["releases.win-x64-stable.json"] = feedBytes;
        transport.Package = package;
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "zip-native-identity-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        VerifiedUpdateSource selected = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        string target = Path.Combine(cache, selected.Manifest.Package.Name);

        // Act
        Outcome<string> result = await selected.DownloadFullAsync(target, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetFiles(cache, "*.partial"), Is.Empty);
    }

    [TestCase("duplicate")]
    [TestCase("traversal")]
    [TestCase("ads")]
    [TestCase("device")]
    [TestCase("unicode")]
    [TestCase("directory")]
    [TestCase("script")]
    [TestCase("second-package")]
    [TestCase("entries")]
    public void Preflight_WhenLeavesAreUnsafe_RejectsBeforeEntryMaterialisation(string fault)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        Dictionary<string, byte[]> entries = Entries(transport);
        entries[fault switch
        {
            "duplicate" => "RELEASES.WIN-X64-STABLE.JSON",
            "traversal" => "../README.txt",
            "ads" => "README.txt:stream",
            "device" => "CON.txt",
            "unicode" => "READMÉ.txt",
            "directory" => "directory/",
            "script" => "install.ps1",
            _ => "Other-full.nupkg"
        }] = [1];
        if (fault == "entries")
        {
            for (int index = 0; index < 33; index++)
            {
                entries["transition-" + index.ToString("X64") + ".json"] = [1];
            }
        }
        using MemoryStream archive = new MemoryStream(FixtureUpdateTransport.Bundle(entries));

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Preflight(archive, Manifest(fixture), CancellationToken.None));
    }

    [TestCase("count")]
    [TestCase("directory")]
    [TestCase("zip64")]
    [TestCase("split")]
    [TestCase("encrypted")]
    [TestCase("descriptor")]
    [TestCase("method")]
    [TestCase("link")]
    [TestCase("reparse")]
    [TestCase("local-crc")]
    [TestCase("local-name")]
    [TestCase("offset")]
    [TestCase("total")]
    [TestCase("trailing")]
    [TestCase("prefix")]
    public void Preflight_WhenHeadersAreHostile_RefusesBoundedly(string fault)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        byte[] bytes = FixtureUpdateTransport.Bundle(Entries(FixtureUpdateTransport.Create(fixture)));
        int end = bytes.Length - 22;
        int directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        switch (fault)
        {
            case "count":
                Put16(bytes, end + 10, 33);
                break;
            case "directory":
                Put32(bytes, end + 12, 65537);
                break;
            case "zip64":
                Put32(bytes, directory + 24, uint.MaxValue);
                break;
            case "split":
                Put16(bytes, end + 4, 1);
                break;
            case "encrypted":
                Put16(bytes, directory + 8, 1);
                break;
            case "descriptor":
                Put16(bytes, directory + 8, 8);
                break;
            case "method":
                Put16(bytes, directory + 10, 9);
                break;
            case "link":
                Put32(bytes, directory + 38, 0xa1ff0000);
                break;
            case "reparse":
                Put32(bytes, directory + 38, 0x400);
                break;
            case "local-crc":
                bytes[14] ^= 1;
                break;
            case "local-name":
                bytes[30] ^= 1;
                break;
            case "offset":
                Put32(bytes, directory + 42, 1);
                break;
            case "total":
                Put32(bytes, directory + 24, 2147483649);
                break;
            case "trailing":
                bytes = [.. bytes, 0];
                break;
            case "prefix":
                bytes = [0, .. bytes];
                break;
        }
        using MemoryStream archive = new MemoryStream(bytes);

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => UpdateBundle.Preflight(archive, Manifest(fixture), CancellationToken.None));
    }

    [TestCase("manifest")]
    [TestCase("signature")]
    [TestCase("feed")]
    [TestCase("package")]
    [TestCase("crc")]
    public async Task DownloadFullAsync_WhenBundleSubstitutesSelectedBytes_RefusesAndCleansOnlyOwnedScratch(string fault)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "zip-substitution-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        VerifiedUpdateSource selected = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        Dictionary<string, byte[]> entries = Entries(transport);
        string name = fault switch
        {
            "manifest" => "Postfold.win-x64-stable.manifest.json",
            "signature" => "Postfold.win-x64-stable.manifest.sig",
            "feed" => "releases.win-x64-stable.json",
            _ => selected.Manifest.Package.Name
        };
        if (fault != "crc")
        {
            entries[name] = [.. entries[name], 1];
        }
        byte[] bundle = FixtureUpdateTransport.Bundle(entries);
        if (fault == "crc")
        {
            int end = bundle.Length - 22;
            int central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(end + 16));
            bundle[14] ^= 1;
            bundle[central + 16] ^= 1;
        }
        transport.BundleBytes = bundle;
        Directory.CreateDirectory(cache);
        string abandoned = Path.Combine(cache, "abandoned.partial");
        File.WriteAllBytes(abandoned, [9]);
        string target = Path.Combine(cache, selected.Manifest.Package.Name);

        // Act
        Outcome<string> result = await selected.DownloadFullAsync(target, CancellationToken.None);
        Outcome<string> retry = await selected.DownloadFullAsync(target, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(retry.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetFiles(cache, "*.partial"), Is.EqualTo(new[] { abandoned }));
        Assert.That(File.ReadAllBytes(abandoned), Is.EqualTo(new byte[] { 9 }));
    }

    [TestCase("files")]
    [TestCase("bytes")]
    [TestCase("directory")]
    [TestCase("lease")]
    public async Task DownloadFullAsync_WhenRetainedStateExhaustsBudget_RefusesBeforeNetwork(string fault)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "zip-quota-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        Directory.CreateDirectory(cache);
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        VerifiedUpdateSource selected = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        using FileStream? lease = fault == "lease" ? new FileStream(Path.Combine(cache, "staging.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None) : null;
        if (fault == "files")
        {
            for (int index = 0; index < 254; index++)
            {
                File.WriteAllBytes(Path.Combine(cache, index + ".partial"), []);
            }
        }
        if (fault == "bytes")
        {
            using FileStream retained = File.Create(Path.Combine(cache, "retained.partial"));
            retained.SetLength(2147483648L - UpdateBundle.MaximumBytes - selected.Manifest.Package.Length + 1);
        }
        if (fault == "directory")
        {
            Directory.CreateDirectory(Path.Combine(cache, "unknown"));
        }

        // Act
        Outcome<string> result = await selected.DownloadFullAsync(Path.Combine(cache, selected.Manifest.Package.Name), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(transport.Downloads, Is.Zero);
    }

    private static Dictionary<string, byte[]> Entries(FixtureUpdateTransport transport)
    {
        Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(transport.Files, StringComparer.Ordinal)
        {
            [ClientUpdateFixture.PackageName] = transport.Package,
            ["README.txt"] = [1],
            ["SHA256SUMS.txt"] = [2],
            ["Postfold-win-x64-stable-Setup.exe"] = [3]
        };
        return entries;
    }

    private static UpdateManifest Manifest(UpdateFixture fixture)
    {
        return UpdateTrust.VerifyManifest(fixture.Manifest(), fixture.Sign(fixture.Manifest()), fixture.PublicKey).Value!;
    }

    private static void Put16(byte[] bytes, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    }

    private static void Put32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }
}

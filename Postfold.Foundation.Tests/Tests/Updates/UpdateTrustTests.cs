using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateTrustTests
{
    private UpdateFixture _fixture = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _fixture = new UpdateFixture();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _fixture.Dispose();
    }

    [Test]
    public void VerifyManifest_WhenExactBytesAreSigned_AcceptsSupportedForwardMigration()
    {
        // Arrange
        byte[] bytes = _fixture.Manifest();

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(UpdateTrust.Compatible(result.Value!, "stable", "1.0.0", 2, UpdateFixture.InitialMigration), Is.True);
        Assert.That(UpdateTrust.Compatible(result.Value!, "stable", "1.0.0", 3, UpdateFixture.TargetMigration), Is.False);
        Assert.That(UpdateTrust.Compatible(result.Value!, "beta", "1.0.0", 2, UpdateFixture.InitialMigration), Is.False);
        Assert.That(UpdateTrust.Compatible(result.Value!, "stable", "0.9.0", 2, UpdateFixture.InitialMigration), Is.False);
        Assert.That(UpdateTrust.Compatible(result.Value!, "stable", "1.1.0", 2, UpdateFixture.InitialMigration), Is.False);
        Assert.That(UpdateTrust.Compatible(result.Value! with
        {
            TargetFormat = 2,
            TargetMigration = UpdateFixture.InitialMigration
        }, "stable", "1.0.0", 2, UpdateFixture.InitialMigration), Is.True);
    }

    [TestCase("whitespace")]
    [TestCase("wrong-key")]
    [TestCase("pkcs1")]
    [TestCase("short-signature")]
    public void VerifyManifest_WhenAuthenticationFails_RejectsExactInput(string change)
    {
        // Arrange
        byte[] bytes = _fixture.Manifest();
        byte[] signature = _fixture.Sign(bytes, change == "pkcs1");
        using UpdateFixture unrelated = new UpdateFixture();
        string key = change == "wrong-key" ? unrelated.PublicKey : _fixture.PublicKey;
        if (change == "whitespace")
        {
            bytes = [.. bytes, (byte)' '];
        }
        if (change == "short-signature")
        {
            signature = signature[..^1];
        }

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, signature, key);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(result.Value, Is.Null);
    }

    [TestCase("product", "Other")]
    [TestCase("rid", "linux-x64")]
    [TestCase("channel", "unknown")]
    [TestCase("toolChannel", "stable")]
    [TestCase("version", "01.1.0")]
    [TestCase("version", "1.1.0+ambiguous")]
    [TestCase("version", "1.1.0-beta.1")]
    [TestCase("version", "2147483648.0.0")]
    [TestCase("minimumApp", "2.0.0")]
    [TestCase("targetMigration", "../wrong")]
    [TestCase("sourceCommit", "not-a-commit")]
    [TestCase("keyId", "BAD")]
    public void VerifyManifest_WhenSignedFieldsAreInvalid_RejectsMetadata(string field, string value)
    {
        // Arrange
        byte[] bytes = _fixture.Manifest(change: document => document[field] = value);

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase("null")]
    [TestCase("unknown")]
    [TestCase("missing")]
    [TestCase("numeric-string")]
    [TestCase("negative")]
    [TestCase("schema")]
    [TestCase("reverse-format")]
    [TestCase("null-origin")]
    [TestCase("duplicate-origin")]
    [TestCase("empty-origin")]
    [TestCase("future-origin")]
    public void VerifyManifest_WhenSignedStructureIsInvalid_ReturnsValidation(string change)
    {
        // Arrange
        byte[] bytes = _fixture.Manifest(change: document =>
        {
            switch (change)
            {
                case "null":
                    document["version"] = null;
                    break;
                case "unknown":
                    document["future"] = true;
                    break;
                case "missing":
                    document.Remove("feed");
                    break;
                case "numeric-string":
                    document["sequence"] = "1";
                    break;
                case "negative":
                    document["sequence"] = -1;
                    break;
                case "schema":
                    document["schema"] = 2;
                    break;
                case "reverse-format":
                    document["targetFormat"] = 1;
                    break;
                case "null-origin":
                    document["originMigrations"] = new JsonArray((JsonNode?)null);
                    break;
                case "duplicate-origin":
                    document["originMigrations"] = new JsonArray(UpdateFixture.InitialMigration, UpdateFixture.InitialMigration);
                    break;
                case "empty-origin":
                    document["originMigrations"] = new JsonArray();
                    break;
                case "future-origin":
                    document["originMigrations"] = new JsonArray("20990101000000_Future");
                    break;
            }
        });

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase("duplicate-root")]
    [TestCase("duplicate-nested")]
    [TestCase("bom")]
    [TestCase("invalid-utf8")]
    [TestCase("oversized")]
    [TestCase("trailing-comma")]
    public void VerifyManifest_WhenSignedEncodingIsInvalid_RejectsBeforeMaterialisingRelease(string change)
    {
        // Arrange
        string json = Encoding.UTF8.GetString(_fixture.Manifest());
        byte[] bytes = change switch
        {
            "duplicate-root" => Encoding.UTF8.GetBytes(json.Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)),
            "duplicate-nested" => Encoding.UTF8.GetBytes(json.Replace("\"length\":", "\"extra\":0,\"extra\":0,\"length\":", StringComparison.Ordinal)),
            "bom" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)],
            "invalid-utf8" => [0xFF, .. Encoding.UTF8.GetBytes(json)],
            "oversized" => Encoding.UTF8.GetBytes(json + new string(' ', 65536)),
            _ => Encoding.UTF8.GetBytes(json[..^1] + ",}")
        };

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(2147483649L)]
    public void VerifyManifest_WhenSignedArtifactLengthExceedsBounds_RejectsIt(long length)
    {
        // Arrange
        byte[] bytes = _fixture.Manifest(change: document => document["package"]!["length"] = length);

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase("../package.nupkg")]
    [TestCase("https://host/package.nupkg")]
    [TestCase("CON.nupkg")]
    [TestCase("safe:stream")]
    [TestCase("safe%2epkg")]
    [TestCase("safe\\pkg")]
    [TestCase("safe.pkg.")]
    public void VerifyManifest_WhenSignedPackageNameIsUnsafe_RejectsIt(string name)
    {
        // Arrange
        byte[] bytes = _fixture.Manifest(change: document => document["package"]!["name"] = name);

        // Act
        Outcome<UpdateManifest> result = UpdateTrust.VerifyManifest(bytes, _fixture.Sign(bytes), _fixture.PublicKey);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase("exact", true)]
    [TestCase("corrupt", false)]
    [TestCase("short", false)]
    [TestCase("long", false)]
    public async Task VerifyAsync_WhenArtifactBytesAreRead_RequiresExactSizeAndHash(string change, bool valid)
    {
        // Arrange
        UpdateManifest manifest = UpdateTrust.InspectManifest(_fixture.Manifest()).Value!;
        byte[] bytes = _fixture.Package.ToArray();
        if (change == "corrupt")
        {
            bytes[0] ^= 1;
        }
        bytes = change == "short" ? bytes[..^1] : change == "long" ? [.. bytes, 0] : bytes;
        using MemoryStream stream = new MemoryStream(bytes);

        // Act
        Failure result = await UpdateArtifacts.VerifyAsync(stream, manifest.Package, CancellationToken.None);

        // Assert
        Assert.That(result == Failure.None, Is.EqualTo(valid));
    }

    [Test]
    public void VerifyFeed_WhenSignedFeedContainsDeltaOrDuplicateFields_RejectsBeforeLibraryParsing()
    {
        // Arrange
        UpdateManifest manifest = UpdateTrust.InspectManifest(_fixture.Manifest()).Value!;
        byte[] delta = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_fixture.Feed).Replace("Full", "Delta", StringComparison.Ordinal));
        byte[] duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_fixture.Feed).Replace("\"Assets\":", "\"Assets\":[],\"Assets\":", StringComparison.Ordinal));

        // Act
        Failure exact = UpdateArtifacts.VerifyFeed(_fixture.Feed, manifest);
        Failure wrongType = UpdateArtifacts.VerifyFeed(delta, manifest with
        {
            Feed = manifest.Feed with
            {
                Length = delta.Length,
                Sha256 = UpdateFixture.Digest(delta)
            }
        });
        Failure repeated = UpdateArtifacts.VerifyFeed(duplicate, manifest with
        {
            Feed = manifest.Feed with
            {
                Length = duplicate.Length,
                Sha256 = UpdateFixture.Digest(duplicate)
            }
        });

        // Assert
        Assert.That(exact, Is.EqualTo(Failure.None));
        Assert.That(wrongType, Is.EqualTo(Failure.Validation));
        Assert.That(repeated, Is.EqualTo(Failure.Validation));
    }
}

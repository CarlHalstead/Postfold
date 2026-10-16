using Postfold.Core.Policies.Updates;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

internal sealed class UpdateFixture : IDisposable
{
    internal const string InitialMigration = "20261014150754_InitialEfProfile";
    internal const string TargetMigration = "20261015000000_UpdateFixture";
    private readonly RSA _key = RSA.Create(3072);

    internal UpdateFixture()
    {
        using MemoryStream stream = new MemoryStream();
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("Postfold.nuspec");
            entry.LastWriteTime = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
            using Stream specification = entry.Open();
            specification.Write(Encoding.UTF8.GetBytes("<package><metadata><id>Postfold</id><version>1.1.0</version></metadata></package>"));
        }
        Package = stream.ToArray();
        Feed = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Assets = new[]
            {
                new
                {
                    PackageId = "Postfold",
                    Version = "1.1.0",
                    Type = "Full",
                    FileName = "Postfold-1.1.0-win-x64-stable-full.nupkg",
                    SHA256 = Digest(Package),
                    Size = Package.Length
                }
            }
        });
    }

    internal string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    internal string KeyId => UpdateTrust.KeyId(PublicKey);
    internal byte[] Package { get; }
    internal byte[] Feed { get; }

    public void Dispose()
    {
        _key.Dispose();
    }

    internal byte[] Manifest(long sequence = 1, string version = "1.1.0", Action<JsonObject>? change = null)
    {
        JsonObject document = new JsonObject
        {
            ["kind"] = "PostfoldUpdateManifest",
            ["schema"] = 1,
            ["keyId"] = KeyId,
            ["product"] = "Postfold",
            ["rid"] = "win-x64",
            ["channel"] = "stable",
            ["toolChannel"] = "win-x64-stable",
            ["sequence"] = sequence,
            ["version"] = version,
            ["sourceCommit"] = new string('a', 40),
            ["minimumApp"] = "1.0.0",
            ["minimumFormat"] = 2,
            ["maximumFormat"] = 2,
            ["targetFormat"] = 3,
            ["originMigrations"] = new JsonArray(InitialMigration),
            ["targetMigration"] = TargetMigration,
            ["feed"] = new JsonObject
            {
                ["name"] = "releases.win-x64-stable.json",
                ["length"] = Feed.Length,
                ["sha256"] = Digest(Feed)
            },
            ["package"] = new JsonObject
            {
                ["name"] = "Postfold-" + version + "-win-x64-stable-full.nupkg",
                ["length"] = Package.Length,
                ["sha256"] = Digest(Package)
            }
        };
        change?.Invoke(document);
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    internal byte[] Transition(UpdateFixture successor, byte[] activation, long epoch = 1, long sequence = 2, string version = "1.2.0")
    {
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "PostfoldKeyTransition",
            schema = 1,
            keyId = KeyId,
            product = "Postfold",
            rid = "win-x64",
            channel = "stable",
            epoch,
            successor = successor.PublicKey,
            activationSequence = sequence,
            activationVersion = version,
            activationSha256 = Digest(activation)
        });
    }

    internal byte[] Sign(byte[] bytes, bool pkcs1 = false)
    {
        return _key.SignData(bytes, HashAlgorithmName.SHA256, pkcs1 ? RSASignaturePadding.Pkcs1 : RSASignaturePadding.Pss);
    }

    internal byte[] EncryptedKey(string password)
    {
        return _key.ExportEncryptedPkcs8PrivateKey(password.AsSpan(), new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200000));
    }

    internal static string Digest(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

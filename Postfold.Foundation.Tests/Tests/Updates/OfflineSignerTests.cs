using NUnit.Framework;
using Postfold.Core.Policies.Updates;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.UpdateSigner.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class OfflineSignerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SignAsync_WhenRotationBindsActivation_InspectsSuccessorSignatureAndArtifacts(bool corruptActivation)
    {
        // Arrange
        using UpdateFixture predecessor = new UpdateFixture();
        using UpdateFixture successor = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        byte[] activation = successor.Manifest();
        byte[] transition = predecessor.Transition(successor, activation, sequence: 1, version: "1.1.0");
        string document = Path.Combine(directory, "transition.json");
        string output = Path.Combine(directory, "transition.sig");
        string key = Path.Combine(directory, "synthetic-predecessor.pk8");
        await File.WriteAllBytesAsync(document, transition);
        await File.WriteAllBytesAsync(key, predecessor.EncryptedKey("synthetic-test-password"));
        await File.WriteAllBytesAsync(Path.Combine(directory, "activation.manifest.json"), activation);
        await File.WriteAllBytesAsync(Path.Combine(directory, "activation.manifest.sig"), corruptActivation ? predecessor.Sign(activation) : successor.Sign(activation));
        await File.WriteAllBytesAsync(Path.Combine(directory, "Postfold-1.1.0-win-x64-stable-full.nupkg"), successor.Package);
        await File.WriteAllBytesAsync(Path.Combine(directory, "releases.win-x64-stable.json"), successor.Feed);

        // Act
        bool signed = await OfflineUpdateSigner.SignAsync(
            document,
            directory,
            key,
            output,
            "synthetic-test-password".AsMemory(),
            true,
            CancellationToken.None
        );

        // Assert
        Assert.That(signed, Is.EqualTo(!corruptActivation));
        if (signed)
        {
            Assert.That(UpdateTrust.VerifyTransition(transition, await File.ReadAllBytesAsync(output), predecessor.PublicKey).IsSuccess, Is.True);
        }
        else
        {
            Assert.That(File.Exists(output), Is.False);
        }
    }

    [TestCase("valid", true)]
    [TestCase("wrong-password", false)]
    [TestCase("changed-package", false)]
    [TestCase("changed-feed", false)]
    [TestCase("wrong-key-id", false)]
    [TestCase("existing-output", false)]
    public async Task SignAsync_WhenExactArtifactsAndEncryptedKeyAreInspected_EmitsOnlyValidNewSignature(string change, bool expected)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        string manifest = Path.Combine(directory, "manifest.json");
        string key = Path.Combine(directory, "synthetic-encrypted.pk8");
        string signature = Path.Combine(directory, "manifest.sig");
        byte[] bytes = fixture.Manifest(change: document =>
        {
            if (change == "wrong-key-id")
            {
                document["keyId"] = new string('A', 64);
            }
        });
        await File.WriteAllBytesAsync(manifest, bytes);
        await File.WriteAllBytesAsync(key, fixture.EncryptedKey("synthetic-test-password"));
        await File.WriteAllBytesAsync(Path.Combine(directory, "Postfold-1.1.0-win-x64-stable-full.nupkg"), change == "changed-package" ? [1, 2, 3] : fixture.Package);
        await File.WriteAllBytesAsync(Path.Combine(directory, "releases.win-x64-stable.json"), change == "changed-feed" ? [1, 2, 3] : fixture.Feed);
        if (change == "existing-output")
        {
            await File.WriteAllBytesAsync(signature, [7, 8, 9]);
        }

        // Act
        bool signed = await OfflineUpdateSigner.SignAsync(
            manifest,
            directory,
            key,
            signature,
            (change == "wrong-password" ? "wrong" : "synthetic-test-password").AsMemory(),
            false,
            CancellationToken.None
        );

        // Assert
        Assert.That(signed, Is.EqualTo(expected));
        if (expected)
        {
            Assert.That(UpdateTrust.VerifyManifest(bytes, await File.ReadAllBytesAsync(signature), fixture.PublicKey).IsSuccess, Is.True);
        }
        else if (change == "existing-output")
        {
            Assert.That(await File.ReadAllBytesAsync(signature), Is.EqualTo(new byte[] { 7, 8, 9 }));
        }
        else
        {
            Assert.That(File.Exists(signature), Is.False);
        }
        Assert.That(await File.ReadAllBytesAsync(manifest), Is.EqualTo(bytes));
    }
}

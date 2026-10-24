using NUnit.Framework;
using Postfold.UpdateBroker.Models;
using Postfold.UpdateBroker.Services;
using System;
using System.IO;
using System.Security.Cryptography;

namespace Postfold.Foundation.Tests.Tests.Updates;

[Platform("Win")]
public sealed class NativeUpdateStagingTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Prepare_WhenExecutablesShipInInstallation_PinsExternalCopiesAndRejectsTamperedReuse(bool tamper)
    {
        // Arrange
        string area = Path.Combine(TestContext.CurrentContext.WorkDirectory, "staged-native-" + Guid.NewGuid().ToString("N"));
        string installation = Path.Combine(area, "Postfold");
        string current = Path.Combine(installation, "current");
        Directory.CreateDirectory(current);
        string broker = Path.Combine(current, "Postfold.UpdateBroker.exe");
        string helper = Path.Combine(installation, "Update.exe");
        byte[] brokerBytes = [1, 2, 3];
        byte[] helperBytes = [4, 5, 6];
        File.WriteAllBytes(broker, brokerBytes);
        File.WriteAllBytes(helper, helperBytes);
        NativeUpdateRequest request = new NativeUpdateRequest(
            1,
            1,
            Convert.ToHexString(SHA256.HashData(brokerBytes)),
            helper,
            Convert.ToHexString(SHA256.HashData(helperBytes)),
            installation,
            "1.0.0",
            new string('A', 64),
            "beta",
            2,
            "migration",
            "root",
            Path.Combine(installation, "updates", "trust.journal"),
            new string('B', 64),
            [],
            [],
            [],
            Path.Combine(installation, "updates", "full.nupkg")
        );

        // Act
        using StagedNativeUpdate staged = StagedNativeUpdate.Prepare(broker, request);
        Directory.Move(current, current + "-replaced");

        // Assert
        Assert.That(staged.Broker.StartsWith(installation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.False);
        Assert.That(staged.Request.HelperPath.StartsWith(installation + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.False);
        Assert.That(File.ReadAllBytes(staged.Broker), Is.EqualTo(brokerBytes));
        Assert.That(File.ReadAllBytes(staged.Request.HelperPath), Is.EqualTo(helperBytes));
        Assert.That(staged.Request.TrustJournal, Is.EqualTo(request.TrustJournal));
        Assert.Throws<IOException>(() => File.WriteAllBytes(staged.Broker, [9]));
        Assert.Throws<IOException>(() => File.WriteAllBytes(staged.Request.HelperPath, [9]));
        staged.Dispose();
        // The installed source stays available under its replacement name for the next admission.
        string retainedBroker = Path.Combine(current + "-replaced", "Postfold.UpdateBroker.exe");
        if (tamper)
        {
            File.WriteAllBytes(staged.Broker, [9]);
            Assert.Throws<InvalidDataException>(() => StagedNativeUpdate.Prepare(retainedBroker, request));
        }
        else
        {
            using StagedNativeUpdate reused = StagedNativeUpdate.Prepare(retainedBroker, request);
            Assert.That(reused.Broker, Is.EqualTo(staged.Broker));
            Assert.That(File.ReadAllBytes(reused.Broker), Is.EqualTo(brokerBytes));
        }
    }
}

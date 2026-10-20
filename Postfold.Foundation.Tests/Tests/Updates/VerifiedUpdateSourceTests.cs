using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Desktop.Services.Updates;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Logging;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class VerifiedUpdateSourceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DownloadFullAsync_WhenFreshOrCachedPackageIsCorrupt_RejectsWithoutPromotion(bool cached)
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        string cache = Path.Combine(directory, "packages");
        Directory.CreateDirectory(cache);
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        VerifiedUpdateSource source = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        byte[] corrupt = fixture.Package.ToArray();
        corrupt[^1] ^= 1;
        string target = Path.Combine(cache, source.Manifest.Package.Name);
        if (cached)
        {
            await File.WriteAllBytesAsync(target, corrupt);
        }
        else
        {
            transport.Package = corrupt;
        }

        // Act
        Outcome<string> result = await source.DownloadFullAsync(target, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(cached ? Failure.Corrupt : Failure.Validation));
        Assert.That(transport.Downloads, Is.EqualTo(cached ? 0 : 1));
        if (cached)
        {
            Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(corrupt));
        }
        else
        {
            Assert.That(File.Exists(target), Is.False);
            Assert.That(Directory.GetFiles(cache, "*.partial"), Is.Empty);
        }
    }

    [Test]
    public async Task DownloadFullAsync_WhenExactReleaseIsSelected_PromotesOnceAndDoesNotTrustMutatedLibraryAssets()
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        VerifiedUpdateSource source = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        IVelopackLogger logger = Substitute.For<IVelopackLogger>();
        VelopackAsset asset = (await source.GetReleaseFeed(logger, "Postfold", "win-x64-stable")).Assets.Single();
        string target = Path.Combine(cache, source.Manifest.Package.Name);

        // Act
        Outcome<string> downloaded = await source.DownloadFullAsync(target, CancellationToken.None);
        Outcome<string> cached = await source.DownloadFullAsync(target, CancellationToken.None);
        asset.SHA256 = new string('0', 64);

        // Assert
        Assert.That(downloaded.IsSuccess, Is.True);
        Assert.That(cached.IsSuccess, Is.True);
        Assert.That(await File.ReadAllBytesAsync(target), Is.EqualTo(fixture.Package));
        Assert.That(transport.Downloads, Is.EqualTo(1));
        Assert.That((await source.GetReleaseFeed(logger, "Postfold", "win-x64-stable")).Assets.Single().SHA256, Is.EqualTo(source.Manifest.Package.Sha256));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.DownloadReleaseEntry(logger, asset, target, _ => { }, CancellationToken.None));
        Assert.That((await source.DownloadFullAsync(Path.Combine(directory, "outside.nupkg"), CancellationToken.None)).Failure, Is.EqualTo(Failure.Validation));
        Assert.That(File.Exists(Path.Combine(directory, "outside.nupkg")), Is.False);
    }

    [Test]
    public async Task DownloadFullAsync_WhenCancelled_RemovesOwnedScratchWithoutMarkingReady()
    {
        // Arrange
        using UpdateFixture fixture = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        string cache = Path.Combine(directory, "packages");
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), fixture.PublicKey, "stable").Value!;
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(fixture);
        VerifiedUpdateSource source = (await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, cache, CancellationToken.None)).Value!;
        using CancellationTokenSource cancel = new CancellationTokenSource();
        transport.DuringDownload = cancel.Cancel;
        string target = Path.Combine(cache, source.Manifest.Package.Name);

        // Act / Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => source.DownloadFullAsync(target, cancel.Token));
        Assert.That(File.Exists(target), Is.False);
        Assert.That(Directory.GetFiles(cache, "*.partial"), Is.Empty);
    }

    [Test]
    public async Task OpenAsync_WhenAuthenticatedRotationIsNeeded_TracesPinnedChainAndExactActivation()
    {
        // Arrange
        using UpdateFixture root = new UpdateFixture();
        using UpdateFixture successor = new UpdateFixture();
        string directory = EngineFixture.DirectoryPath();
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(directory, "trust"), root.PublicKey, "stable").Value!;
        FixtureUpdateTransport transport = FixtureUpdateTransport.Create(successor);
        byte[] activation = successor.Manifest();
        byte[] transition = root.Transition(successor, activation, sequence: 1, version: "1.1.0");
        string transitionName = "transition-" + root.KeyId;
        string activationName = "activation-" + UpdateFixture.Digest(activation);
        transport.Files[transitionName + ".json"] = transition;
        transport.Files[transitionName + ".sig"] = root.Sign(transition);
        transport.Files[activationName + ".json"] = activation;
        transport.Files[activationName + ".sig"] = successor.Sign(activation);

        // Act
        Outcome<VerifiedUpdateSource> opened = await VerifiedUpdateSource.OpenAsync(transport, trust, "1.0.0", 2, UpdateFixture.InitialMigration, Path.Combine(directory, "packages"), CancellationToken.None);

        // Assert
        Assert.That(opened.IsSuccess, Is.True);
        Assert.That(trust.State.Epoch, Is.EqualTo(1));
        Assert.That(trust.State.Sequence, Is.EqualTo(1));
        Assert.That(trust.State.Pending, Is.Null);
        Assert.That(trust.State.PublicKey, Is.EqualTo(successor.PublicKey));
    }
}

using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateTrustStoreTests
{
    private UpdateFixture _old = null!;
    private UpdateFixture _next = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _old = new UpdateFixture();
        _next = new UpdateFixture();
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _old.Dispose();
        _next.Dispose();
    }

    [Test]
    public void Accept_WhenReopened_RejectsReplayEquivocationAndVersionRollback()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "trust.journal");
        byte[] accepted = _old.Manifest(4, "1.4.0");
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            Assert.That(store.Accept(accepted, _old.Sign(accepted)).IsSuccess, Is.True);
        }
        byte[] replay = _old.Manifest(3, "1.3.0");
        byte[] equivocation = [.. accepted, (byte)' '];
        byte[] rollback = _old.Manifest(5, "1.3.0");

        // Act
        using UpdateTrustStore reopened = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!;
        long length = new FileInfo(path).Length;
        Outcome<UpdateTrustState> identical = reopened.Accept(accepted, _old.Sign(accepted));
        Outcome<UpdateTrustState> stale = reopened.Accept(replay, _old.Sign(replay));
        Outcome<UpdateTrustState> changed = reopened.Accept(equivocation, _old.Sign(equivocation));
        Outcome<UpdateTrustState> olderVersion = reopened.Accept(rollback, _old.Sign(rollback));

        // Assert
        Assert.That(reopened.State.Sequence, Is.EqualTo(4));
        Assert.That(identical.IsSuccess, Is.True);
        Assert.That(stale.Failure, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(changed.Failure, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(olderVersion.Failure, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(length));
    }

    [Test]
    public void Accept_WhenRotationIsReopened_RequiresExactActivationAndRetiresPredecessor()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "rotation.journal");
        byte[] initial = _old.Manifest();
        byte[] activation = _next.Manifest(2, "1.2.0");
        byte[] transition = _old.Transition(_next, activation);
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            Assert.That(store.Accept(initial, _old.Sign(initial)).IsSuccess, Is.True);
            Assert.That(store.Accept(transition, _old.Sign(transition), transition: true).IsSuccess, Is.True);
        }
        byte[] skipped = _next.Manifest(3, "1.3.0");

        // Act
        using (UpdateTrustStore pending = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            Assert.That(pending.State.Epoch, Is.EqualTo(1));
            Assert.That(pending.State.Pending, Is.Not.Null);
            Assert.That(pending.Accept(initial, _old.Sign(initial)).Failure, Is.EqualTo(Failure.Validation));
            Assert.That(pending.Accept(skipped, _next.Sign(skipped)).Failure, Is.EqualTo(Failure.PolicyBlocked));
            Assert.That(pending.Accept(activation, _next.Sign(activation)).IsSuccess, Is.True);
        }
        using UpdateTrustStore reopened = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!;
        Outcome<UpdateTrustState> later = reopened.Accept(skipped, _next.Sign(skipped));

        // Assert
        Assert.That(reopened.State.Pending, Is.Null);
        Assert.That(reopened.State.PublicKey, Is.EqualTo(_next.PublicKey));
        Assert.That(reopened.State.Sequence, Is.EqualTo(3));
        Assert.That(later.IsSuccess, Is.True);
        Assert.That(reopened.Accept(transition, _old.Sign(transition), transition: true).Failure, Is.EqualTo(Failure.Validation));
    }

    [TestCase(0)]
    [TestCase(2)]
    public void AcceptTransition_WhenEpochIsNotTheImmediateSuccessor_LeavesTrustUnchanged(long epoch)
    {
        // Arrange
        byte[] activation = _next.Manifest(2, "1.2.0");
        byte[] transition = _old.Transition(_next, activation, epoch);
        UpdateTrustState state = new UpdateTrustState(_old.PublicKey, "stable");

        // Act
        Outcome<UpdateTrustState> result = state.AcceptTransition(transition, _old.Sign(transition));

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(state.Epoch, Is.Zero);
        Assert.That(state.PublicKey, Is.EqualTo(_old.PublicKey));
    }

    [TestCase("product", "Other")]
    [TestCase("rid", "linux-x64")]
    [TestCase("channel", "beta")]
    [TestCase("kind", "PostfoldUpdateManifest")]
    [TestCase("successor", "invalid-key")]
    [TestCase("activationSha256", "bad-hash")]
    public void AcceptTransition_WhenSignedContextIsWrong_RefusesIt(string field, string value)
    {
        // Arrange
        byte[] activation = _next.Manifest(2, "1.2.0");
        JsonObject document = JsonNode.Parse(_old.Transition(_next, activation))!.AsObject();
        document[field] = value;
        byte[] transition = Encoding.UTF8.GetBytes(document.ToJsonString());
        UpdateTrustState state = new UpdateTrustState(_old.PublicKey, "stable");

        // Act
        Outcome<UpdateTrustState> result = state.AcceptTransition(transition, _old.Sign(transition));

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(state.PublicKey, Is.EqualTo(_old.PublicKey));
    }

    [Test]
    public void Open_WhenJournalHasTornFrameOrChangedSignature_BlocksWithoutDiscardingEvidence()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "complete.journal");
        byte[] release = _old.Manifest();
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            Assert.That(store.Accept(release, _old.Sign(release)).IsSuccess, Is.True);
        }
        byte[] complete = File.ReadAllBytes(path);
        int headerLength = Encoding.ASCII.GetByteCount("PostfoldUpdateTrust/1:" + _old.KeyId + ":stable\n");

        // Act / Assert: every possible interrupted append boundary must refuse, never truncate to an older watermark.
        foreach (int boundary in new[] { 0, 1, headerLength - 1, headerLength, headerLength + 1, headerLength + 4,
            headerLength + 5, headerLength + 5 + release.Length, complete.Length - 1 })
        {
            string tornPath = path + ".torn-" + boundary;
            byte[] prefix = complete[..boundary];
            File.WriteAllBytes(tornPath, prefix);
            File.WriteAllBytes(tornPath + ".head", File.ReadAllBytes(path + ".head"));
            Outcome<UpdateTrustStore> opened = UpdateTrustStore.Open(tornPath, _old.PublicKey, "stable");
            Assert.That(opened.Failure, Is.EqualTo(Failure.NeedsReview), "Boundary " + boundary);
            Assert.That(File.ReadAllBytes(tornPath), Is.EqualTo(prefix));
        }
        byte[] changed = complete.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(path + ".corrupt", changed);
        File.WriteAllBytes(path + ".corrupt.head", File.ReadAllBytes(path + ".head"));
        Assert.That(UpdateTrustStore.Open(path + ".corrupt", _old.PublicKey, "stable").Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(complete));
    }

    [Test]
    public void Open_WhenCrashLeavesAFlushedFrameBeforeHeadPromotion_RecoversForwardOnly()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "head-recovery.journal");
        byte[] initial = _old.Manifest();
        byte[] activation = _next.Manifest(2, "1.2.0");
        byte[] transition = _old.Transition(_next, activation);
        byte[] oldHead;
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            Assert.That(store.Accept(initial, _old.Sign(initial)).IsSuccess, Is.True);
            oldHead = File.ReadAllBytes(path + ".head");
            Assert.That(store.Accept(transition, _old.Sign(transition), transition: true).IsSuccess, Is.True);
        }
        byte[] committedHead = File.ReadAllBytes(path + ".head");
        File.WriteAllBytes(path + ".head", oldHead);

        // Act
        using UpdateTrustStore recovered = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!;

        // Assert
        Assert.That(recovered.State.Epoch, Is.EqualTo(1));
        Assert.That(recovered.State.PublicKey, Is.EqualTo(_next.PublicKey));
        Assert.That(recovered.State.Pending, Is.Not.Null);
        Assert.That(File.ReadAllBytes(path + ".head"), Is.EqualTo(committedHead));
        Assert.That(recovered.Accept(initial, _old.Sign(initial)).IsSuccess, Is.False);
    }

    [Test]
    public void Open_WhenHeadIsCorrupt_PreservesJournalAndRefusesTrust()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "corrupt-head.journal");
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            byte[] release = _old.Manifest();
            Assert.That(store.Accept(release, _old.Sign(release)).IsSuccess, Is.True);
        }
        byte[] journal = File.ReadAllBytes(path);
        File.WriteAllBytes(path + ".head", [0, 1, 2]);

        // Act
        Outcome<UpdateTrustStore> opened = UpdateTrustStore.Open(path, _old.PublicKey, "stable");

        // Assert
        Assert.That(opened.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(journal));
        Assert.That(File.ReadAllBytes(path + ".head"), Is.EqualTo(new byte[] { 0, 1, 2 }));
    }

    [Test]
    public void Accept_WhenCheckpointPromotionFails_BlocksFurtherUseAndRecoversOnlyForward()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "write-failure.journal");
        byte[] document = _old.Manifest();
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            store.Checkpoint = stage =>
            {
                if (stage == "head-prepared")
                {
                    throw new IOException("Synthetic checkpoint disk failure.");
                }
            };

            // Act
            Outcome<UpdateTrustState> failure = store.Accept(document, _old.Sign(document));

            // Assert
            Assert.That(failure.Failure, Is.EqualTo(Failure.Unavailable));
            Assert.That(store.State.Sequence, Is.Zero);
            Assert.That(store.Accept(document, _old.Sign(document)).Failure, Is.EqualTo(Failure.NeedsReview));
        }
        using UpdateTrustStore recovered = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!;
        Assert.That(recovered.State.Sequence, Is.EqualTo(1));
    }

    [Test]
    public void Open_WhenJournalIsMissingButCheckpointSurvives_RefusesReinitialisation()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "missing.journal");
        byte[] evidence = [1, 2, 3];
        File.WriteAllBytes(path + ".head", evidence);

        // Act
        Outcome<UpdateTrustStore> opened = UpdateTrustStore.Open(path, _old.PublicKey, "stable");

        // Assert
        Assert.That(opened.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(File.Exists(path), Is.False);
        Assert.That(File.ReadAllBytes(path + ".head"), Is.EqualTo(evidence));
    }

    [Test]
    public void Open_WhenAnotherOwnerOrWrongRootOrChannelIsPresent_RefusesIndependentTrust()
    {
        // Arrange
        string path = Path.Combine(EngineFixture.DirectoryPath(), "exclusive.journal");
        using (UpdateTrustStore store = UpdateTrustStore.Open(path, _old.PublicKey, "stable").Value!)
        {
            // Act / Assert
            Assert.That(UpdateTrustStore.Open(path, _old.PublicKey, "stable").Failure, Is.EqualTo(Failure.Unavailable));
        }
        Assert.That(UpdateTrustStore.Open(path, _next.PublicKey, "stable").Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(UpdateTrustStore.Open(path, _old.PublicKey, "beta").Failure, Is.EqualTo(Failure.NeedsReview));
    }
}

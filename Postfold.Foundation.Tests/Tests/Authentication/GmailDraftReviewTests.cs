using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Outbox;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Storage.Stores;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class GmailDraftReviewTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task CopyReviewedDraftAsync_WhenOutboxIsAcceptedOrUncertain_DoesNotChangeSubmissionEvidence(
        bool uncertain
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        ComposeContent local = (await runtime.Application.ReadDraftAsync(held, CancellationToken.None)).Value!;
        Guid submission = (await runtime.Find(held.Account)!.QueueSubmissionAsync(local, null, null, CancellationToken.None)).Value!.Id;
        provider.LoseSendAcknowledgement = uncertain;
        await runtime.Application.SubmitAsync(held.Account, submission, CancellationToken.None);
        OutboxEntry before = (await runtime.Catalog.OutboxAsync(held.Account, CancellationToken.None)).Single();
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;

        // Act
        Assert.That(await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None), Is.EqualTo(Failure.None));
        await runtime.Application.ProcessOutboxAsync(held.Account, CancellationToken.None);
        OutboxEntry after = (await runtime.Catalog.OutboxAsync(held.Account, CancellationToken.None)).Single();

        // Assert
        Assert.That(after.State, Is.EqualTo(before.State));
        Assert.That(after.Revision, Is.EqualTo(before.Revision));
        Assert.That(after.Evidence!.Recipients, Is.EqualTo(before.Evidence!.Recipients));
        Assert.That(after.Submission.Mime, Is.EqualTo(before.Submission.Mime));
        Assert.That(after.Submission.Sender, Is.EqualTo(before.Submission.Sender));
        Assert.That(after.Submission.Recipients, Is.EqualTo(before.Submission.Recipients));
        Assert.That(after.Submission.Id, Is.EqualTo(before.Submission.Id));
        Assert.That(provider.Sends, Is.EqualTo(1));
        Assert.That(provider.DraftWrites, Is.EqualTo(2));
        Assert.That(provider.Requests.Any(item => item.Method is "PUT" or "DELETE"), Is.False);
    }

    [Test]
    public async Task ReviewDraftAsync_WhenInspectionIsCancelled_PreservesHeldWorkWithoutRemoteCreate()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        provider.Intercept = async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return null;
        };

        // Act
        await Assert.ThatAsync(async () => await runtime.Application.ReviewDraftAsync(held, cancel.Token), Throws.InstanceOf<OperationCanceledException>());

        // Assert
        Assert.That((await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(), Is.EqualTo(held));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CopyReviewedDraftAsync_WhenAcknowledgementIsKnownOrLost_PreservesOriginalsAndNeverReplaysAfterRestart(
        bool lost
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        DraftReviewSnapshot review;
        ComposeContent local;
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
            review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
            local = (await runtime.Application.ReadDraftAsync(held, CancellationToken.None)).Value!;
            provider.LoseDraftAcknowledgement = lost;

            // Act
            Assert.That(await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None), Is.EqualTo(lost ? Failure.NeedsReview : Failure.None));
            Assert.That(await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        }
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        DraftSnapshot[] drafts = (await reopened.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).ToArray();
        DraftSnapshot copy = drafts.Single(item => item.Id == review.CopyId);
        await reopened.Application.PushDraftAsync(copy.Account, copy.Id, CancellationToken.None);
        await reopened.Application.SynchroniseAsync(copy.Account, CancellationToken.None);
        await reopened.Application.ProcessOutboxAsync(copy.Account, CancellationToken.None);

        // Assert
        Assert.That(drafts.Single(item => item.Id == review.Draft.Id), Is.EqualTo(review.Draft));
        Assert.That(copy.Held, Is.EqualTo(lost));
        Assert.That(copy.Remote?.Key, Is.EqualTo(lost ? null : "draft-2"));
        ComposeContent restored = (await reopened.Application.ReadDraftAsync(copy, CancellationToken.None)).Value!;
        Assert.That(restored.PlainText, Is.EqualTo(local.PlainText));
        Assert.That(restored.Attachments, Is.EqualTo(local.Attachments));
        ComposeContent remote = (await reopened.Application.ReadDraftAsync(review.Draft with { Content = review.RemoteContent }, CancellationToken.None)).Value!;
        Assert.That(remote.PlainText, Does.Contain("original remote text"));
        Assert.That(remote.Attachments, Has.Length.EqualTo(1));
        Assert.That(remote.Attachments[0].Content.Protected, Is.True);
        await using Stream attachment = await ((SqliteProfile)reopened.Catalog).Blobs.OpenAsync(remote.Attachments[0].Content, CancellationToken.None);
        using StreamReader attachmentText = new StreamReader(attachment);
        Assert.That(await attachmentText.ReadToEndAsync(), Is.EqualTo("protected attachment bytes"));
        Assert.That(await reopened.Catalog.DraftVersionsAsync(copy.Account, review.Draft.Id, CancellationToken.None), Has.Length.GreaterThanOrEqualTo(3));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 2);
    }

    [Test]
    public async Task CopyReviewedDraftAsync_WhenOriginalsChangeAfterRecheck_DoesNotAcknowledgeOrOverwriteTheirEdits()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
        provider.Intercept = async (request, _) =>
        {
            if (request.Method == System.Net.Http.HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/drafts", StringComparison.Ordinal))
            {
                ComposeContent content = (await runtime.Application.ReadDraftAsync(held, CancellationToken.None)).Value!;
                await runtime.Application.SaveDraftAsync(held.Account, held.Id, content with { PlainText = "edit after recheck" }, CancellationToken.None);
                provider.ReplaceDraft("draft-1", GmailFixture.Mime + "remote edit after recheck");
            }
            return null;
        };

        // Act
        Failure result = await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        DraftSnapshot[] drafts = (await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).ToArray();
        DraftSnapshot current = drafts.Single(item => item.Id == held.Id);
        Assert.That(current.Revision, Is.EqualTo(held.Revision + 1));
        Assert.That(current.PushedRevision, Is.EqualTo(held.PushedRevision));
        Assert.That(current.Remote, Is.EqualTo(held.Remote));
        Assert.That(current.Held, Is.True);
        Assert.That((await runtime.Application.ReadDraftAsync(current, CancellationToken.None)).Value!.PlainText, Is.EqualTo("edit after recheck"));
        Assert.That((await runtime.Application.ReadDraftAsync(drafts.Single(item => item.Id == review.CopyId), CancellationToken.None)).Value!.PlainText, Is.EqualTo("reviewed local text"));
        DraftReviewSnapshot fresh = (await runtime.Application.ReviewDraftAsync(current, CancellationToken.None)).Value!;
        Assert.That((await runtime.Application.ReadDraftAsync(current with { Content = fresh.RemoteContent }, CancellationToken.None)).Value!.PlainText, Does.Contain("remote edit after recheck"));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 2);
    }

    [Test]
    public async Task ReviewDraftAsync_WhenVersionRetentionFails_DoesNotExposeDecisionOrCreateRemoteDraft()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder

        {
            DataSource = Path.Combine(directory, "profile.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_retention_fault BEFORE INSERT ON DraftVersions BEGIN SELECT RAISE(ABORT,'synthetic retention fault'); END;";
        await command.ExecuteNonQueryAsync();

        // Act
        await Assert.ThrowsAsync<SqliteException>(async () => await runtime.Application.ReviewDraftAsync(held, CancellationToken.None));

        // Assert
        Assert.That((await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(), Is.EqualTo(held));
        Assert.That(await runtime.Catalog.DraftVersionsAsync(held.Account, held.Id, CancellationToken.None), Has.Length.EqualTo(3));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 1);
    }

    [TestCase("account")]
    [TestCase("identity")]
    [TestCase("revision")]
    [TestCase("version")]
    [TestCase("decision")]
    [TestCase("blob")]
    public async Task CopyReviewedDraftAsync_WhenDecisionIsForeignOrStale_RejectsWithoutCreatingDraft(
        string changed
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
        DraftReviewSnapshot decision = changed switch
        {
            "account" => review with { Draft = held with { Account = new AccountId(2) } },
            "identity" => review with { Draft = held with { Remote = new ProviderIdentity(held.Account, "foreign", null) } },
            "revision" => review with { Draft = held with { Revision = held.Revision + 1 } },
            "version" => review with { RemoteVersion = "not-reviewed" },
            "decision" => review with { CopyId = Guid.NewGuid() },
            _ => review with { RemoteContent = held.Content }
        };

        // Act
        Failure result = await runtime.Find(held.Account)!.CopyReviewedDraftAsync(decision, CancellationToken.None);

        // Assert
        Assert.That(result, Is.AnyOf(Failure.Validation, Failure.NeedsReview));
        Assert.That((await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(), Is.EqualTo(held));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CopyReviewedDraftAsync_WhenEitherVersionChangesDuringInspection_LeavesNewerWorkUnacknowledged(
        bool remote
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(EngineFixture.DirectoryPath(), provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
        provider.Intercept = async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/drafts/draft-1", StringComparison.Ordinal))
            {
                if (remote)
                {
                    provider.ReplaceDraft("draft-1", GmailFixture.Mime + "new remote edit");
                }
                else
                {
                    ComposeContent content = (await runtime.Application.ReadDraftAsync(held, CancellationToken.None)).Value!;
                    await runtime.Application.SaveDraftAsync(held.Account, held.Id, content with { PlainText = "concurrent local edit" }, CancellationToken.None);
                }
            }
            return null;
        };

        // Act
        Failure result = await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        DraftSnapshot current = (await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single();
        Assert.That(current.PushedRevision, Is.EqualTo(held.PushedRevision));
        Assert.That(current.Held, Is.True);
        Assert.That(current.Revision, Is.EqualTo(remote ? held.Revision : held.Revision + 1));
        GmailDraftReviewFixture.AssertSafeEffects(provider, 1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CopyReviewedDraftAsync_WhenCancellationOrShutdownFollowsCreate_RetainsDurableUncertainty(
        bool shutdown
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
        provider.DraftEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        Task<Failure> creating = runtime.Application.CopyReviewedDraftAsync(review, cancel.Token);
        try
        {
            await provider.DraftEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            if (shutdown)
            {
                await runtime.DisposeAsync();
            }
            else
            {
                await cancel.CancelAsync();
            }
            try
            {
                await creating.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
            await runtime.DisposeAsync();
            await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
            DraftSnapshot copy = (await reopened.Catalog.DraftsAsync(held.Account, CancellationToken.None)).Single(item => item.Id == review.CopyId);
            await reopened.Application.PushDraftAsync(copy.Account, copy.Id, CancellationToken.None);

            // Assert
            Assert.That(copy.Held, Is.True);
            Assert.That(copy.PushedRevision, Is.Zero);
            Assert.That(copy.Remote, Is.Null);
            GmailDraftReviewFixture.AssertSafeEffects(provider, 2);
        }
        finally
        {
            await cancel.CancelAsync();
            try
            {
                await creating.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [TestCase("copy")]
    [TestCase("acknowledgement")]
    public async Task CopyReviewedDraftAsync_WhenPersistenceFails_PreservesHoldAndDoesNotRepeatRemoteEffect(
        string boundary
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await using DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
        DraftReviewSnapshot review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder

        {
            DataSource = Path.Combine(directory, "profile.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER synthetic_review_fault BEFORE " + (boundary == "copy" ? "INSERT" : "UPDATE") +
            " ON Drafts BEGIN SELECT RAISE(ABORT,'synthetic storage fault'); END;";
        await command.ExecuteNonQueryAsync();

        // Act
        await Assert.ThrowsAsync<SqliteException>(async () => await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None));
        command.CommandText = "DROP TRIGGER synthetic_review_fault;";
        await command.ExecuteNonQueryAsync();
        DraftSnapshot[] drafts = (await runtime.Catalog.DraftsAsync(held.Account, CancellationToken.None)).ToArray();
        if (boundary == "acknowledgement")
        {
            DraftSnapshot copy = drafts.Single(item => item.Id == review.CopyId);
            Assert.That(copy.Held, Is.True);
            await runtime.Application.PushDraftAsync(copy.Account, copy.Id, CancellationToken.None);
            Assert.That(await runtime.Application.CopyReviewedDraftAsync(review, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        }

        // Assert
        Assert.That(drafts.Single(item => item.Id == held.Id), Is.EqualTo(held));
        Assert.That(drafts, Has.Length.EqualTo(boundary == "copy" ? 1 : 2));
        GmailDraftReviewFixture.AssertSafeEffects(provider, boundary == "copy" ? 1 : 2);
    }

    [Test]
    public async Task CopyReviewedDraftAsync_WhenProcessStopsBeforeDispatch_ReopensAsHeldWithoutCreate()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        DraftReviewSnapshot review;
        await using (DesktopMailRuntime runtime = await GmailDraftReviewFixture.OpenAsync(directory, provider))
        {
            DraftSnapshot held = await GmailDraftReviewFixture.HoldAsync(runtime, provider);
            review = (await runtime.Application.ReviewDraftAsync(held, CancellationToken.None)).Value!;
            Assert.That(await ((IDraftStore)runtime.Catalog).CopyReviewedAsync(review, CancellationToken.None), Is.Not.Null);
        }

        // Act
        await using DesktopMailRuntime reopened = await GmailDraftReviewFixture.OpenAsync(directory, provider);
        DraftSnapshot copy = (await reopened.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == review.CopyId);
        Failure result = await reopened.Application.PushDraftAsync(copy.Account, copy.Id, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.NeedsReview));
        Assert.That(copy.Held, Is.True);
        GmailDraftReviewFixture.AssertSafeEffects(provider, 1);
    }
}

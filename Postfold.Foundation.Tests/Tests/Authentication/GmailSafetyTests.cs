using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Services.Authentication;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class GmailSafetyTests
{
    [Test]
    public async Task RunAsync_WhenCancellationFollowsPossibleAcceptance_PersistsUncertaintyAndNeverSendsAgain()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture

        {
            SendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        await using (GmailAdapter adapter = Adapter(provider, auth, profile))
        {
            FrozenSubmission submission = await QueueAsync(profile);
            Task<Failure> sending = new OutboxWork(submission.Account, profile, profile.Blobs, adapter).RunAsync(cancellation.Token);
            await provider.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await cancellation.CancelAsync();
            Assert.That(await sending.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(Failure.NeedsReview));
        }
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await reopened.RecoverAsync(CancellationToken.None);
        await using GmailAdapter restored = Adapter(provider, auth, reopened);
        await new OutboxWork(EngineFixture.Account, reopened, reopened.Blobs, restored).RunAsync(CancellationToken.None);

        // Assert
        OutboxEntry saved = (await reopened.OutboxAsync(EngineFixture.Account, CancellationToken.None)).Single();
        Assert.That(saved.State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(saved.Evidence!.Phase, Is.EqualTo(SubmissionPhase.AwaitingAcknowledgement));
        Assert.That(provider.Sends, Is.EqualTo(1));
    }

    [TestCase(HttpStatusCode.BadRequest, SubmissionState.Rejected, Failure.Validation)]
    [TestCase(HttpStatusCode.Unauthorized, SubmissionState.Rejected, Failure.NeedsAuthentication)]
    [TestCase(HttpStatusCode.Forbidden, SubmissionState.Rejected, Failure.PolicyBlocked)]
    [TestCase(HttpStatusCode.TooManyRequests, SubmissionState.Uncertain, Failure.NeedsReview)]
    [TestCase(HttpStatusCode.ServiceUnavailable, SubmissionState.Uncertain, Failure.NeedsReview)]
    public async Task SubmitAsync_WhenProviderReturnsFailure_DistinguishesKnownRejectionFromPossibleAcceptance(
        HttpStatusCode status,
        SubmissionState state,
        Failure failure
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/send", StringComparison.Ordinal) ?
            GmailFixture.Reply("{\"error\":{\"message\":\"synthetic-private\"}}", status) : null);
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        FrozenSubmission submission = await QueueAsync(profile);
        await using Stream raw = await profile.Blobs.OpenAsync(submission.Mime, CancellationToken.None);

        // Act
        SubmissionOutcome result = await adapter.SubmitAsync(submission, raw, CancellationToken.None);

        // Assert
        Assert.That(result.State, Is.EqualTo(state));
        Assert.That(result.Failure, Is.EqualTo(failure));
        Assert.That(result.Phase, Is.EqualTo(state == SubmissionState.Uncertain ? SubmissionPhase.AwaitingAcknowledgement : SubmissionPhase.Acknowledged));
        Assert.That(result.Recipients.All(item => item.ReplyCode is null), Is.True);
        Assert.That(provider.Requests.Count(item => item.Path == "messages/send"), Is.EqualTo(1));
    }

    [Test]
    public async Task OpenAsync_WhenMimeHasAttachment_UsesSharedParserAndSelectedPartCopy()
    {
        // Arrange
        const string Mime = "From: sender@synthetic.invalid\r\nTo: first@synthetic.invalid\r\nSubject: synthetic attachment\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=test\r\n\r\n--test\r\nContent-Type: text/plain\r\n\r\nsynthetic body\r\n--test\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=synthetic.txt\r\nContent-Transfer-Encoding: base64\r\n\r\nc3ludGhldGljLWF0dGFjaG1lbnQ=\r\n--test--\r\n";
        await using GmailFixture provider = new GmailFixture
        {
            Raw = GmailFixture.Encode(Mime)
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        MimePipeline pipeline = new MimePipeline(1048576, 10, profile.Blobs);
        ProviderIdentity identity = new ProviderIdentity(EngineFixture.Account, "one", null);
        Outcome<Stream> opened = await adapter.OpenAsync(identity, CancellationToken.None);
        await using Stream raw = opened.Value!;

        // Act
        Outcome<MimeSnapshot> parsed = await pipeline.ParseAsync(raw, CancellationToken.None);
        raw.Position = 0;
        using MemoryStream copied = new MemoryStream();
        Outcome<bool> extracted = await pipeline.CopyAttachmentAsync(raw, parsed.Value!.Attachments.Single().Part, copied, CancellationToken.None);
        raw.Position = 0;
        Outcome<MimeSnapshot> tooMany = await new MimePipeline(1048576, 1).ParseAsync(raw, CancellationToken.None);

        // Assert
        Assert.That(extracted.IsSuccess, Is.True);
        Assert.That(System.Text.Encoding.UTF8.GetString(copied.ToArray()), Is.EqualTo("synthetic-attachment"));
        Assert.That(tooMany.Failure, Is.EqualTo(Failure.Validation));
        Assert.That((await adapter.OpenAsync(identity with { Account = new AccountId(2) }, CancellationToken.None)).Failure, Is.EqualTo(Failure.Validation));
    }

    [Test]
    public async Task OpenAsync_WhenDecodedMimeExceedsLimit_RejectsBeforeAllocatingDecodedPayload()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture
        {
            Raw = GmailFixture.Encode(new string('x', 101))
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = new GmailAdapter(EngineFixture.Account, GmailFixture.Binding, auth, profile.Blobs, 100, TimeProvider.System, provider.Http());

        // Act
        Outcome<Stream> result = await adapter.OpenAsync(new ProviderIdentity(EngineFixture.Account, "one", null), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Corrupt));
    }

    [Test]
    public async Task ReadAsync_WhenCommitIsCancelled_LeavesCheckpointAndCanonicalDataTogether()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        CanonicalPage page = (await adapter.ReadAsync(null, 10, CancellationToken.None)).Value!;
        using CancellationTokenSource cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        // Act
        await Assert.CatchAsync<OperationCanceledException>(async () => await profile.CommitAccountAsync(page, cancelled.Token));

        // Assert
        Assert.That(await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None), Is.Null);
        Assert.That(await profile.CachedMessageCountAsync(CancellationToken.None), Is.Zero);
        await profile.CommitAccountAsync(page, CancellationToken.None);
        Assert.That(await profile.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(1));
        Assert.That(await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None), Is.EqualTo(page.Checkpoint));
    }

    [Test]
    public async Task AcquireAsync_WhenConcurrentRefreshRotatesAndCacheReopens_RetainsExactScopeAndMailboxBinding()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        provider.Authentication.TokenJson = provider.Authentication.TokenJson.Replace("3600", "30", StringComparison.Ordinal);
        await provider.SeedAsync();
        provider.Authentication.TokenJson = provider.Authentication.TokenJson.Replace("30,", "3600,", StringComparison.Ordinal)
            .Replace("synthetic-access", "synthetic-rotated-access", StringComparison.Ordinal)
            .Replace("synthetic-refresh", "synthetic-rotated-refresh", StringComparison.Ordinal);
        await using GoogleAccountAuthenticator auth = provider.Authenticator();

        // Act
        Outcome<AccessToken>[] tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.AcquireAsync(false, CancellationToken.None)));
        await using GoogleAccountAuthenticator reopened = provider.Authenticator();
        Outcome<AccessToken> retained = await reopened.AcquireAsync(false, CancellationToken.None);
        await reopened.DeleteCredentialsAsync(CancellationToken.None);
        provider.Authentication.Subject = "other@synthetic.invalid";
        Outcome<AccessToken> substituted = await reopened.AcquireAsync(true, CancellationToken.None);

        // Assert
        Assert.That(tokens.All(item => item.Value!.Secret == "synthetic-rotated-access"), Is.True);
        Assert.That(retained.Value!.Subject, Is.EqualTo(GmailFixture.Binding.Address));
        Assert.That(provider.Authentication.TokenRequests.Count(item => item.Contains("grant_type=refresh_token", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(substituted.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(provider.Authentication.Stored, Is.Empty);
        Assert.That(AuthenticationFixture.Query(provider.Authentication.Authorization!)["scope"], Is.EqualTo(GoogleAccountAuthenticator.Scope));
    }

    [Test]
    public async Task AcquireAsync_WhenWindowsTokenCacheReopens_UsesNativeProtectionWithoutPasswordStorage()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows DPAPI qualification runs on Windows.");
        }
        await using GmailFixture provider = new GmailFixture();
        WindowsTokenStore store = new WindowsTokenStore(Path.Combine(EngineFixture.DirectoryPath(), "google-tokens"));
        await using (GoogleAccountAuthenticator initial = new GoogleAccountAuthenticator(
            EngineFixture.Account,
            GmailFixture.Binding.ClientId,
            store,
            provider.Authentication,
            provider.Authentication.Http,
            TimeProvider.System,
            TimeSpan.FromSeconds(15),
            GmailFixture.Binding.Address
        ))
        {
            Assert.That((await initial.AcquireAsync(true, CancellationToken.None)).IsSuccess, Is.True);
        }
        await using GoogleAccountAuthenticator restored = new GoogleAccountAuthenticator(
            EngineFixture.Account,
            GmailFixture.Binding.ClientId,
            store,
            provider.Authentication,
            provider.Authentication.Http,
            TimeProvider.System,
            TimeSpan.FromSeconds(15),
            GmailFixture.Binding.Address
        );
        Uri? browser = provider.Authentication.Authorization;

        // Act
        Outcome<AccessToken> result = await restored.AcquireAsync(false, CancellationToken.None);

        // Assert
        Assert.That(result.Value!.Subject, Is.EqualTo(GmailFixture.Binding.Address));
        Assert.That(provider.Authentication.Authorization, Is.SameAs(browser));
        Assert.That(provider.Authentication.TokenRequests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RunAsync_WhenProcessStopsAfterApiAcceptanceBeforeCommit_RecoversAsUncertainWithoutSecondSubmission()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        await using (GmailAdapter adapter = Adapter(provider, auth, profile))
        {
            FrozenSubmission submission = await QueueAsync(profile);
            OutboxEntry claimed = (await ((IOutboxStore)profile).ClaimAsync(submission.Account, CancellationToken.None))!;
            await using Stream raw = await profile.Blobs.OpenAsync(claimed.Submission.Mime, CancellationToken.None);

            // Act: the provider accepts, but no FinishAsync evidence reaches the database.
            Assert.That((await adapter.SubmitAsync(claimed.Submission, raw, CancellationToken.None)).State, Is.EqualTo(SubmissionState.Accepted));
        }
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await reopened.RecoverAsync(CancellationToken.None);
        await using GmailAdapter restored = Adapter(provider, auth, reopened);
        await new OutboxWork(EngineFixture.Account, reopened, reopened.Blobs, restored).RunAsync(CancellationToken.None);

        // Assert
        Assert.That((await reopened.OutboxAsync(EngineFixture.Account, CancellationToken.None)).Single().State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(provider.Sends, Is.EqualTo(1));
    }

    [Test]
    public async Task RunAsync_WhenLocalRevisionChangesDuringRemoteCreate_AcknowledgesOnlyTransmittedRevisionThenUpdatesTheSameDraft()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        Guid id = Guid.NewGuid();
        BlobReference first = await EngineFixture.BlobAsync(profile, GmailFixture.Mime);
        await profile.SaveAsync(id, EngineFixture.Account, first, CancellationToken.None);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Intercept = async (request, token) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/drafts", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        };
        DraftWork work = new DraftWork(id, EngineFixture.Account, profile, profile.Blobs, adapter);
        Task<Failure> pushing = work.RunAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            BlobReference newer = await EngineFixture.BlobAsync(profile, GmailFixture.Mime + "new local revision");
            await profile.SaveAsync(id, EngineFixture.Account, newer, CancellationToken.None);
            release.TrySetResult();
            Assert.That(await pushing.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(Failure.None));
            DraftSnapshot afterCreate = (await profile.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;
            Assert.That(afterCreate.Revision, Is.EqualTo(2));
            Assert.That(afterCreate.PushedRevision, Is.EqualTo(1));
            Assert.That(afterCreate.Content, Is.EqualTo(newer));
            Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));

            // Assert
            DraftSnapshot held = (await profile.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;
            Assert.That(held.Held, Is.False);
            Assert.That(held.Conflict, Is.False);
            Assert.That(held.Content, Is.EqualTo(newer));
            Assert.That(provider.DraftWrites, Is.EqualTo(1));
            Assert.That(provider.DraftUpdates, Is.EqualTo(1));
        }
        finally
        {
            release.TrySetResult();
            await pushing.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static GmailAdapter Adapter(
        GmailFixture provider,
        GoogleAccountAuthenticator auth,
        SqliteProfile profile
    )
    {
        return new GmailAdapter(EngineFixture.Account, GmailFixture.Binding, auth, profile.Blobs, 1048576, TimeProvider.System, provider.Http());
    }

    private static async Task<FrozenSubmission> QueueAsync(
        SqliteProfile profile
    )
    {
        BlobReference blob = await EngineFixture.BlobAsync(profile, GmailFixture.Mime);
        FrozenSubmission submission = EngineFixture.Submission(blob) with { Recipients = ["first@synthetic.invalid"] };
        await profile.QueueAsync(submission, CancellationToken.None);
        return submission;
    }
}

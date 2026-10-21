using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services.Authentication;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class GmailAdapterTests
{
    [Test]
    public async Task RunAsync_WhenEnumerationOverlapsHistory_UsesOneCanonicalMessageAndReplaysChangesBeforeAbsence()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        provider.AfterList = page =>
        {
            if (page == 1)
            {
                provider.Messages["three"] = ["INBOX"];
                provider.Messages["one"] = ["Label_1", "STARRED"];
                provider.History = "{\"historyId\":\"900\",\"history\":[{\"id\":\"888\",\"messagesAdded\":[{\"message\":{\"id\":\"three\"}}],\"labelsRemoved\":[{\"message\":{\"id\":\"one\"},\"labelIds\":[\"INBOX\",\"UNREAD\"]}]}]}";
            }
        };

        // Act
        await CompleteAsync(adapter, profile);
        MessageSnapshot[] all = await SearchAsync(profile);
        Checkpoint checkpoint = (await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None))!;
        MailboxSnapshot label = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "Label_1");
        SearchPage labelled = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", label.Id, false, 100), CancellationToken.None)).Value!;

        // Assert
        Assert.That(all.Select(item => item.Provider.Key), Is.EquivalentTo(new[] { "one", "two", "three" }));
        Assert.That(all.Select(item => item.Id).Distinct().Count(), Is.EqualTo(3));
        Assert.That(all.Single(item => item.Provider.Key == "one").NativeThreadId, Is.EqualTo("thread-one"));
        Assert.That(all.Single(item => item.Provider.Key == "one").Flagged, Is.True);
        Assert.That(labelled.Messages.Single().Id, Is.EqualTo(all.Single(item => item.Provider.Key == "one").Id));
        Assert.That(checkpoint.Complete, Is.True);
        Assert.That(checkpoint.Cursor, Does.Contain("900"));
        Assert.That(provider.Requests.Any(item => item.Path.Contains("startHistoryId=100", StringComparison.Ordinal)), Is.True);
        Assert.That(provider.Requests.Where(item => item.Path.StartsWith("messages?", StringComparison.Ordinal)).All(item => item.Path.Contains("includeSpamTrash=true", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public async Task RunAsync_WhenHistoryExpiresAndPageFails_RestartsGenerationWithoutLosingIntentOrUnseenMail()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        string? retainedCursor;
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        await using (GmailAdapter adapter = Adapter(provider, auth, profile))
        {
            await CompleteAsync(adapter, profile);
            MessageSnapshot one = (await SearchAsync(profile)).Single(item => item.Provider.Key == "one");
            await profile.QueueAsync(new MutationIntent(new OperationId(Guid.NewGuid()), one.Account, one.Id, one.Provider, MutationKind.SetRead, true, null), CancellationToken.None);
            bool expired = false;
            provider.Intercept = (request, _) =>
            {
                string path = request.RequestUri!.PathAndQuery;
                HttpResponseMessage? result = null;
                if (path.Contains("/history?", StringComparison.Ordinal) && !expired)
                {
                    expired = true;
                    result = GmailFixture.Reply("{}", HttpStatusCode.NotFound);
                }
                else if (path.Contains("pageToken=1", StringComparison.Ordinal))
                {
                    result = GmailFixture.Reply("{}", HttpStatusCode.ServiceUnavailable);
                }
                return Task.FromResult(result);
            };
            CanonicalReconciliationWork work = new CanonicalReconciliationWork(adapter, profile, 10);

            // Act
            Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));
            retainedCursor = (await profile.ReadCheckpointAsync(one.Account, "$account", CancellationToken.None))!.Cursor;
            Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.Transient));
            Assert.That((await profile.ReadCheckpointAsync(one.Account, "$account", CancellationToken.None))!.Cursor, Is.EqualTo(retainedCursor));
            Assert.That(await SearchAsync(profile), Has.Length.EqualTo(2));
        }
        provider.Intercept = null;
        provider.Pages = [["one"], []];
        provider.Messages.Remove("two");
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await using GmailAdapter restored = Adapter(provider, auth, reopened);
        await CompleteAsync(restored, reopened);

        // Assert
        MessageSnapshot retained = (await SearchAsync(reopened)).Single();
        Assert.That(retained.Provider.Key, Is.EqualTo("one"));
        Assert.That(retained.Read, Is.True);
        Assert.That((await reopened.ReadNextAsync(retained.Account, CancellationToken.None))!.Intent.Provider.Key, Is.EqualTo("one"));
    }

    [Test]
    public async Task RunAsync_WhenHistoryHasDuplicateEventsAndDeletion_ReplaysPagesWithoutAdvancingPastFailure()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        await CompleteAsync(adapter, profile);
        provider.Messages["one"] = ["Label_1"];
        provider.Messages.Remove("two");
        provider.History = "{\"historyId\":\"999\",\"nextPageToken\":\"history-next\",\"history\":[{\"id\":\"555\",\"messages\":[{\"id\":\"one\"},{\"id\":\"one\"}],\"messagesDeleted\":[{\"message\":{\"id\":\"two\"}}]}]}";
        CanonicalReconciliationWork work = new CanonicalReconciliationWork(adapter, profile, 10);

        // Act
        Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));
        Checkpoint intermediate = (await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None))!;
        provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.Query.Contains("pageToken=history-next", StringComparison.Ordinal) ?
            GmailFixture.Reply("{}", HttpStatusCode.ServiceUnavailable) : null);
        Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.Transient));
        Assert.That(await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None), Is.EqualTo(intermediate));
        provider.Intercept = null;
        provider.History = "{\"historyId\":\"1001\"}";
        await CompleteAsync(adapter, profile);

        // Assert
        Assert.That(intermediate.Complete, Is.False);
        Assert.That(intermediate.Cursor, Does.Contain("120"));
        Assert.That((await SearchAsync(profile)).Single().Provider.Key, Is.EqualTo("one"));
        MailboxSnapshot inbox = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "INBOX");
        Assert.That((await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", inbox.Id, false, 100), CancellationToken.None)).Value!.Messages, Is.Empty);
    }

    [TestCase("loop")]
    [TestCase("foreign-account")]
    [TestCase("version")]
    public async Task ReadAsync_WhenCursorIsUnsafe_DoesNotCommitOrFinishGeneration(
        string mode
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        CanonicalPage first = (await adapter.ReadAsync(null, 10, CancellationToken.None)).Value!;
        Checkpoint cursor = first.Checkpoint;
        if (mode == "foreign-account")
        {
            cursor = cursor with { Account = new AccountId(2) };
        }
        else if (mode == "version")
        {
            cursor = cursor with { Cursor = cursor.Cursor!.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal) };
        }
        else
        {
            provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) ?
                GmailFixture.Reply("{\"messages\":[],\"nextPageToken\":\"1\"}") : null);
        }

        // Act
        Outcome<CanonicalPage> result = await adapter.ReadAsync(cursor, 10, CancellationToken.None);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None), Is.Null);
    }

    [TestCase("+")]
    [TestCase("a")]
    [TestCase("SGVsbG8===")]
    [TestCase("SGVs bG8")]
    [TestCase("SGVsbG9")]
    public async Task OpenAsync_WhenBase64UrlIsMalformed_RejectsBeforeMimeMaterialisation(
        string raw
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture
        {
            Raw = raw
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);

        // Act
        Outcome<Stream> result = await adapter.OpenAsync(new ProviderIdentity(EngineFixture.Account, "one", null), CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Corrupt));
    }

    [TestCase("json")]
    [TestCase("oversize")]
    [TestCase("redirect")]
    public async Task DiscoverAsync_WhenEnvelopeIsHostile_ReturnsSafeFailureWithoutFollowingHosts(
        string mode
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        provider.Intercept = (_, _) =>
        {
            HttpResponseMessage reply = GmailFixture.Reply(
                mode == "json" ? "[]" : mode == "oversize" ? new string('x', 4194305) : "{}",
                mode == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK
            );
            reply.Headers.Location = new Uri("https://untrusted.invalid/credential-trap");
            return Task.FromResult<HttpResponseMessage?>(reply);
        };

        // Act
        Outcome<System.Collections.Immutable.ImmutableArray<MailboxSnapshot>> result = await adapter.DiscoverAsync(CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(mode == "redirect" ? Failure.PolicyBlocked : Failure.Corrupt));
        Assert.That(provider.Requests, Has.Count.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task RunAsync_WhenSendIsAcceptedOrUncertain_PersistsFrozenEvidenceAndNeverSubmitsAgain(
        bool lost,
        bool malformed
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture
        {
            LoseSendAcknowledgement = lost,
            MalformedSendAcknowledgement = malformed
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        Guid id;
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        await using (GmailAdapter adapter = Adapter(provider, auth, profile))
        {
            FrozenSubmission submission = await QueueAsync(profile);
            id = submission.Id;
            OutboxWork work = new OutboxWork(submission.Account, profile, profile.Blobs, adapter);

            // Act
            await work.RunAsync(CancellationToken.None);
            await work.RunAsync(CancellationToken.None);
            if (!lost && !malformed)
            {
                Assert.That(await new SentCopyWork(id, submission.Account, profile, profile.Blobs, adapter).RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));
            }
            OutboxEntry saved = (await ((IOutboxStore)profile).ReadAsync(id, submission.Account, CancellationToken.None))!;

            // Assert
            Assert.That(saved.Submission.Mime, Is.EqualTo(submission.Mime));
            Assert.That(saved.State, Is.EqualTo(lost || malformed ? SubmissionState.Uncertain : SubmissionState.Completed));
            Assert.That(saved.Evidence!.Recipients.All(item => item.State == (lost || malformed ? RecipientState.Uncertain : RecipientState.Accepted)), Is.True);
            Assert.That(saved.Evidence.Recipients.All(item => item.ReplyCode is null), Is.True);
        }
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await using GmailAdapter restored = Adapter(provider, auth, reopened);
        await new OutboxWork(EngineFixture.Account, reopened, reopened.Blobs, restored).RunAsync(CancellationToken.None);
        Assert.That(provider.Sends, Is.EqualTo(1));
        Assert.That(provider.SentRaw, Is.EqualTo(GmailFixture.Encode(GmailFixture.Mime)));
        Assert.That(provider.Requests.Count(item => item.Method != "GET"), Is.EqualTo(1));
    }

    [TestCase(Failure.CredentialStorageUnavailable)]
    [TestCase(Failure.Corrupt)]
    [TestCase(Failure.NeedsAuthentication)]
    [TestCase(Failure.PolicyBlocked)]
    public async Task RunAsync_WhenAuthenticationFailsBeforeSend_PreservesNotAcceptedWithoutRemoteEffects(
        Failure failure
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        provider.Authentication.StoreFailure = failure;
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        FrozenSubmission submission = await QueueAsync(profile);

        // Act
        await new OutboxWork(submission.Account, profile, profile.Blobs, adapter).RunAsync(CancellationToken.None);
        OutboxEntry saved = (await ((IOutboxStore)profile).ReadAsync(submission.Id, submission.Account, CancellationToken.None))!;

        // Assert
        Assert.That(saved.Evidence!.Failure, Is.EqualTo(failure));
        Assert.That(saved.Evidence.Phase, Is.EqualTo(SubmissionPhase.BeforeConnect));
        Assert.That(saved.Evidence.Recipients.All(item => item.State == RecipientState.NotAccepted), Is.True);
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.Requests, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunAsync_WhenDraftCreateIsUncertainOrReplacementNeedsReview_RetainsLocalAndRemoteVersionsAcrossRestart(
        bool lost
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture
        {
            LoseDraftAcknowledgement = lost
        };
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        Guid id = Guid.NewGuid();
        await using (SqliteProfile profile = await EngineFixture.OpenAsync(directory))
        await using (GmailAdapter adapter = Adapter(provider, auth, profile))
        {
            BlobReference first = await EngineFixture.BlobAsync(profile, GmailFixture.Mime);
            await profile.SaveAsync(id, EngineFixture.Account, first, CancellationToken.None);
            DraftWork work = new DraftWork(id, EngineFixture.Account, profile, profile.Blobs, adapter);

            // Act
            await work.RunAsync(CancellationToken.None);
            if (!lost)
            {
                DraftSnapshot acknowledged = (await profile.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;
                Assert.That(acknowledged.Remote!.Key, Is.EqualTo("draft-1"));
                Assert.That(acknowledged.RemoteVersion, Does.StartWith("v1:draft-message-1:"));
                provider.ReplaceDraft("draft-1", GmailFixture.Mime + "remote revision");
                BlobReference changed = await EngineFixture.BlobAsync(profile, GmailFixture.Mime + "local revision");
                await profile.SaveAsync(id, EngineFixture.Account, changed, CancellationToken.None);
                await work.RunAsync(CancellationToken.None);
                Assert.That(await profile.DraftVersionsAsync(EngineFixture.Account, id, CancellationToken.None), Has.Length.EqualTo(3));
            }
            await work.RunAsync(CancellationToken.None);
        }
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        await using GmailAdapter restored = Adapter(provider, auth, reopened);
        await new DraftWork(id, EngineFixture.Account, reopened, reopened.Blobs, restored).RunAsync(CancellationToken.None);

        // Assert
        DraftSnapshot saved = (await reopened.ReadAsync(id, EngineFixture.Account, CancellationToken.None))!;
        Assert.That(saved.Held, Is.True);
        Assert.That(saved.Conflict, Is.EqualTo(!lost));
        Assert.That(provider.DraftWrites, Is.EqualTo(1));
        Assert.That(provider.Requests.Any(item => item.Method == "PUT"), Is.False);
    }

    [Test]
    public async Task ApplyAsync_WhenLabelsAreChanged_ArchivesWithoutInventingFolderOrPermittingImmutableLabels()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        ProviderIdentity identity = new ProviderIdentity(EngineFixture.Account, "one", null);
        MutationIntent intent = new MutationIntent(new OperationId(Guid.NewGuid()), identity.Account, default, identity, MutationKind.Move, true, null);

        // Act
        Assert.That(await adapter.ApplyAsync(intent, CancellationToken.None), Is.EqualTo(Failure.None));
        Assert.That(await adapter.ApplyAsync(intent with { Kind = MutationKind.SetRead }, CancellationToken.None), Is.EqualTo(Failure.None));
        Assert.That(await adapter.ApplyAsync(intent with { Kind = MutationKind.SetFlagged }, CancellationToken.None), Is.EqualTo(Failure.None));
        string[] afterArchive = provider.Messages["one"];
        Assert.That(await adapter.ApplyAsync(intent with { Destination = "SENT" }, CancellationToken.None), Is.EqualTo(Failure.Unsupported));
        Assert.That(await adapter.ApplyAsync(intent with { Destination = "TRASH" }, CancellationToken.None), Is.EqualTo(Failure.None));
        Assert.That(await adapter.ApplyAsync(intent with { Account = new AccountId(2) }, CancellationToken.None), Is.EqualTo(Failure.Corrupt));

        // Assert
        Assert.That(afterArchive, Is.EquivalentTo(new[] { "Label_1", "STARRED" }));
        Assert.That(provider.Messages["one"], Does.Contain("TRASH"));
        Assert.That(provider.Requests.Any(item => item.Method == "DELETE" || item.Path.Contains("Archive", StringComparison.Ordinal)), Is.False);
    }

    [TestCase("remove", false)]
    [TestCase("delete", false)]
    [TestCase("shutdown", false)]
    [TestCase("remove", true)]
    [TestCase("delete", true)]
    [TestCase("shutdown", true)]
    public async Task StopAsync_WhenGoogleWorkIsActive_CancelsAndAwaitsApiAndRefreshOwners(
        string action,
        bool refresh
    )
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        if (refresh)
        {
            provider.Authentication.TokenJson = provider.Authentication.TokenJson.Replace("3600", "30", StringComparison.Ordinal);
        }
        await provider.SeedAsync();
        await SeedProfileAsync(directory);
        DesktopMailRuntime runtime = await RuntimeAsync(directory, provider);
        bool disposed = false;
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Authentication.BeforeRequest = async (request, token) =>
        {
            if (refresh ? request.RequestUri!.Host == "oauth2.googleapis.com" : request.RequestUri!.AbsolutePath.EndsWith("/labels", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                try
                {
                    await blocked.Task.WaitAsync(token);
                }
                finally
                {
                    stopped.TrySetResult();
                }
            }
        };
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        try
        {
            await runtime.RestoreAccountsAsync(passwords, provider.Authentication, provider.Authentication, CancellationToken.None);
            Task<Failure> reconciliation = runtime.Find(EngineFixture.Account)!.ReconcileAsync("INBOX", CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            if (action == "remove")
            {
                Assert.That(await runtime.RemoveAsync(EngineFixture.Account, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(Failure.None));
            }
            else if (action == "delete")
            {
                Assert.That(await runtime.DeleteGoogleCredentialsAsync(EngineFixture.Account, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(Failure.None));
            }
            else
            {
                await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                disposed = true;
            }
            try
            {
                await reconciliation.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // The cancelled scheduled request is observed by the caller as well as the owner.
            }

            // Assert
            Assert.That(stopped.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(reconciliation.IsCompleted, Is.True);
            Assert.That(provider.Authentication.TokenRequests, Has.Count.EqualTo(1));
            Assert.That(provider.Authentication.Stored.Count, Is.EqualTo(action == "delete" ? 0 : 1));
            Assert.That(passwords.ReceivedCalls(), Is.Empty);
        }
        finally
        {
            if (!disposed)
            {
                await runtime.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task RestoreAccountsAsync_WhenGoogleProfileReopens_RetainsBindingCacheAndProtectedWorkAfterTokenDeletion()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await SeedProfileAsync(directory);
        ICredentialStore passwords = Substitute.For<ICredentialStore>();
        await using (DesktopMailRuntime runtime = await RuntimeAsync(directory, provider))
        {
            await runtime.RestoreAccountsAsync(passwords, provider.Authentication, provider.Authentication, CancellationToken.None);
            IMailAccountEngine engine = runtime.Find(EngineFixture.Account)!;

            // Act
            Assert.That(await engine.ReconcileAsync("INBOX", CancellationToken.None), Is.EqualTo(Failure.None));
            MessageSnapshot one = (await runtime.Query.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", null, false, 10), CancellationToken.None)).Value!.Messages[0];
            Assert.That(await engine.FetchBodyAsync(one, CancellationToken.None), Is.EqualTo(Failure.None));
            using MemoryStream content = new MemoryStream(Encoding.UTF8.GetBytes("protected local draft"));
            Assert.That((await engine.SaveDraftAsync(Guid.NewGuid(), content, CancellationToken.None)).IsSuccess, Is.True);
            Assert.That(await runtime.DeleteGoogleCredentialsAsync(EngineFixture.Account, CancellationToken.None), Is.EqualTo(Failure.None));
        }
        await using (DesktopMailRuntime reopened = await RuntimeAsync(directory, provider))
        {
            await reopened.RestoreAccountsAsync(passwords, provider.Authentication, provider.Authentication, CancellationToken.None);
            AccountConfiguration account = (await reopened.Catalog.AccountsAsync(CancellationToken.None)).Single();

            // Assert
            Assert.That(account.ProviderKind, Is.EqualTo("Google"));
            Assert.That(account.GoogleBinding, Is.EqualTo(GmailFixture.Binding));
            Assert.That(await reopened.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.EqualTo(2));
            Assert.That(await reopened.Catalog.DraftsAsync(account.Id, CancellationToken.None), Has.Length.EqualTo(1));
            Assert.That(await reopened.Find(account.Id)!.ReconcileAsync("INBOX", CancellationToken.None), Is.EqualTo(Failure.NeedsAuthentication));
            Assert.That(passwords.ReceivedCalls(), Is.Empty);
        }
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        Assert.That(await profile.BindGoogleAccountAsync(EngineFixture.Account, GmailFixture.Binding with { Address = "other@synthetic.invalid" }, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        Assert.That((await profile.AccountsAsync(CancellationToken.None)).Single(item => item.Id == EngineFixture.Account).GoogleBinding, Is.EqualTo(GmailFixture.Binding));
    }

    [TestCase(HttpStatusCode.Unauthorized, Failure.NeedsAuthentication)]
    [TestCase(HttpStatusCode.Forbidden, Failure.PolicyBlocked)]
    [TestCase(HttpStatusCode.TooManyRequests, Failure.Transient)]
    public async Task DiscoverAsync_WhenProviderRejectsAccess_ClassifiesSafelyAndHonoursRetryAfter(
        HttpStatusCode status,
        Failure expected
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        provider.Intercept = (_, _) =>
        {
            HttpResponseMessage reply = GmailFixture.Reply("{\"error\":{\"message\":\"synthetic-private-content\"}}", status);
            if (status == HttpStatusCode.TooManyRequests)
            {
                reply.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            }
            return Task.FromResult<HttpResponseMessage?>(reply);
        };

        // Act
        Failure first = (await adapter.DiscoverAsync(CancellationToken.None)).Failure;
        Failure second = (await adapter.DiscoverAsync(CancellationToken.None)).Failure;

        // Assert
        Assert.That(first, Is.EqualTo(expected));
        Assert.That(second, Is.EqualTo(expected));
        Assert.That(provider.Requests.Count, Is.EqualTo(status == HttpStatusCode.TooManyRequests ? 1 : 2));
    }

    [Test]
    public async Task SubmitAsync_WhenEnvelopeDiffersFromFrozenHeaders_RefusesWithoutSending()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = Adapter(provider, auth, profile);
        FrozenSubmission submission = await QueueAsync(profile);
        await using Stream frozen = await profile.Blobs.OpenAsync(submission.Mime, CancellationToken.None);

        // Act
        SubmissionOutcome result = await adapter.SubmitAsync(submission with { Recipients = ["different@synthetic.invalid"] }, frozen, CancellationToken.None);

        // Assert
        Assert.That(result.Failure, Is.EqualTo(Failure.Validation));
        Assert.That(provider.Sends, Is.Zero);
    }

    private static async Task SeedProfileAsync(
        string directory
    )
    {
        await using SqliteProfile profile = (await SqliteProfile.OpenAsync(Path.Combine(directory, "profile.db"), TimeProvider.System, CancellationToken.None)).Value!;
        await profile.AddAccountAsync(EngineFixture.Account, "Synthetic Gmail", CancellationToken.None);
        MailEndpoint placeholder = new MailEndpoint("gmail.googleapis.com", 443, TlsMode.Implicit, GmailFixture.Binding.Address, "unused");
        await profile.ConfigureAccountAsync(EngineFixture.Account, placeholder, placeholder, CancellationToken.None);
        await profile.SaveMailboxesAsync(EngineFixture.Account, [EngineFixture.Mailbox(0)], null, CancellationToken.None);
        Assert.That(await profile.BindGoogleAccountAsync(EngineFixture.Account, GmailFixture.Binding, CancellationToken.None), Is.EqualTo(Failure.None));
    }

    private static async Task<DesktopMailRuntime> RuntimeAsync(
        string directory,
        GmailFixture provider
    )
    {
        DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(true, false, false),
            TimeProvider.System,
            new EngineLimits(2, 32, 1, 10, 1048576, 50, 0, 0, 1048576),
            CancellationToken.None
        )).Value!;
        runtime.GoogleHttpFactory = provider.Http;
        return runtime;
    }

    private static GmailAdapter Adapter(
        GmailFixture provider,
        GoogleAccountAuthenticator auth,
        SqliteProfile profile,
        long limit = 1048576
    )
    {
        return new GmailAdapter(EngineFixture.Account, GmailFixture.Binding, auth, profile.Blobs, limit, TimeProvider.System, provider.Http());
    }

    private static async Task CompleteAsync(
        GmailAdapter adapter,
        SqliteProfile profile
    )
    {
        CanonicalReconciliationWork work = new CanonicalReconciliationWork(adapter, profile, 10);
        for (int page = 0; page < 10; page++)
        {
            Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));
            if ((await profile.ReadCheckpointAsync(adapter.Account, "$account", CancellationToken.None))!.Complete)
            {
                return;
            }
        }
        Assert.Fail("Synthetic enumeration did not finish within its page bound.");
    }

    private static async Task<MessageSnapshot[]> SearchAsync(
        SqliteProfile profile
    )
    {
        return (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", null, false, 100), CancellationToken.None)).Value!.Messages.ToArray();
    }

    private static async Task<FrozenSubmission> QueueAsync(
        SqliteProfile profile
    )
    {
        BlobReference blob = await EngineFixture.BlobAsync(profile, GmailFixture.Mime);
        FrozenSubmission submission = EngineFixture.Submission(blob) with { Recipients = ["first@synthetic.invalid"] };
        Assert.That(await profile.QueueAsync(submission, CancellationToken.None), Is.True);
        return submission;
    }
}

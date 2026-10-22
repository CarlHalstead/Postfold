using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Services.Authentication;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class GmailConcurrentReadTests
{
    [Test]
    public async Task FetchBodyAsync_WhenMetadataResponseIsPending_ShowsSelectedMailAndCachesItWithoutWaitingForSync()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GoogleAccountAuthenticator authentication = provider.Authenticator();
        await using GmailAdapter adapter = new GmailAdapter(EngineFixture.Account, GmailFixture.Binding, authentication, profile.Blobs, 33554432, TimeProvider.System, provider.Http());
        CanonicalPage first = (await adapter.ReadAsync(null, 200, CancellationToken.None)).Value!;
        await profile.CommitAccountAsync(first, CancellationToken.None);
        MessageSnapshot selected = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", null, false, 10), CancellationToken.None)).Value!.Messages[0];
        TaskCompletionSource metadataEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Intercept = async (request, cancellationToken) =>
        {
            if (request.RequestUri!.PathAndQuery.Contains("/messages?", StringComparison.Ordinal))
            {
                metadataEntered.TrySetResult();
                await releaseMetadata.Task.WaitAsync(cancellationToken);
            }
            return (HttpResponseMessage?)null;
        };
        IWorkPolicy policy = Substitute.For<IWorkPolicy>();
        policy.AllowNetwork.Returns(true);
        IRetryDelayPolicy retry = Substitute.For<IRetryDelayPolicy>();
        await using AccountScheduler scheduler = new AccountScheduler(2, 256, 1, TimeProvider.System, policy, retry);
        MimePipeline mime = new MimePipeline(33554432, 200, profile.Blobs);
        await using MailAccountEngine engine = new MailAccountEngine(
            adapter,
            adapter,
            null,
            null,
            null,
            adapter,
            profile,
            profile,
            profile,
            profile,
            profile,
            profile.Blobs,
            mime,
            scheduler,
            policy,
            retry,
            TimeProvider.System,
            new EngineLimits(2, 256, 1, 200, 33554432, 200, 30, 0, 1073741824),
            profile,
            await profile.MailboxesAsync(CancellationToken.None)
        );
        Task<Failure> syncing = engine.ReconcileAsync("INBOX", CancellationToken.None);
        await metadataEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        Failure result;
        bool syncStillPending;
        try
        {
            result = await engine.FetchBodyAsync(selected, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            elapsed.Stop();
            syncStillPending = !syncing.IsCompleted;
        }
        finally
        {
            releaseMetadata.TrySetResult();
        }
        Failure syncResult = await syncing.WaitAsync(TimeSpan.FromSeconds(10));
        Outcome<BlobReference> cached = await profile.ReadBodyAsync(selected.Account, selected.Id, CancellationToken.None);
        await using Stream body = await profile.Blobs.OpenAsync(cached.Value!, CancellationToken.None);
        using StreamReader reader = new StreamReader(body);
        string content = await reader.ReadToEndAsync();

        // Assert
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(syncStillPending, Is.True);
        Assert.That(syncResult, Is.EqualTo(Failure.None));
        Assert.That(content, Does.Contain("frozen synthetic body"));
        TestContext.Progress.WriteLine("Selected Gmail body cached while metadata remained blocked: " + elapsed.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
    }
}

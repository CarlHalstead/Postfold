using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Services.Authentication;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Authentication;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.Protocols.Adapters;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services.Work;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Authentication;

public sealed class GmailQuotaTests
{
    [Test]
    public async Task RunAsync_WhenOldLabelMailIsOnALaterPage_ImportsItBeforeCompletingCoverage()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        await provider.SeedAsync();
        provider.Messages.Clear();
        provider.Messages.Add("recent-one", ["INBOX"]);
        provider.Messages.Add("recent-two", ["INBOX"]);
        provider.Messages.Add("old", ["Label_1"]);
        provider.Pages = [["recent-one"], ["recent-two"], ["old"]];
        provider.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/old", StringComparison.Ordinal) ?
            GmailFixture.Reply("{\"id\":\"old\",\"threadId\":\"old-thread\",\"labelIds\":[\"Label_1\"],\"internalDate\":\"1514764800000\",\"sizeEstimate\":200,\"payload\":{\"headers\":[]}}") : null);
        await using GoogleAccountAuthenticator auth = provider.Authenticator();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(EngineFixture.DirectoryPath());
        await using GmailAdapter adapter = new GmailAdapter(EngineFixture.Account, GmailFixture.Binding, auth, profile.Blobs, 33554432, TimeProvider.System, provider.Http());
        CanonicalReconciliationWork work = new CanonicalReconciliationWork(adapter, profile, 200);
        Checkpoint? checkpoint;

        // Act
        do
        {
            Assert.That(await work.RunAsync(CancellationToken.None), Is.EqualTo(Failure.None));
            checkpoint = await profile.ReadCheckpointAsync(EngineFixture.Account, "$account", CancellationToken.None);
        } while (checkpoint is { Complete: false });
        MailboxSnapshot label = (await profile.MailboxesAsync(CancellationToken.None)).Single(item => item.RemoteKey == "Label_1");
        SearchPage result = (await profile.SearchAsync(new SearchRequest(EngineFixture.Account, false, "", label.Id, false, 100), CancellationToken.None)).Value!;

        // Assert
        Assert.That(checkpoint!.Complete, Is.True);
        Assert.That(result.Messages.Single().Provider.Key, Is.EqualTo("old"));
        Assert.That(result.Messages.Single().Date.Year, Is.EqualTo(2018));
        Assert.That(provider.Requests.Count(item => item.Path.StartsWith("messages?", StringComparison.Ordinal)), Is.EqualTo(3));
    }

    [TestCase("messages/one?format=metadata", 250)]
    [TestCase("messages/one?format=raw", 750)]
    public async Task RequestAsync_WhenReadingRepeatedly_PacesTheActualQuotaCost(
        string path,
        int milliseconds
    )
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        using GmailApiClient api = Client(provider, clock);
        using JsonDocument first = await api.RequestAsync(HttpMethod.Get, path, null, 65536, CancellationToken.None);

        // Act
        Task<JsonDocument> next = api.RequestAsync(HttpMethod.Get, path, null, 65536, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds - 1));

        // Assert
        Assert.That(next.IsCompleted, Is.False);
        Assert.That(provider.Requests, Has.Count.EqualTo(1));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using JsonDocument second = await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(provider.Requests, Has.Count.EqualTo(2));
    }

    [TestCase("rateLimitExceeded")]
    [TestCase("userRateLimitExceeded")]
    public async Task RequestAsync_WhenMetadataIsRateLimited_ResumesTheSameReadAfterCooldown(string reason)
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        int attempts = 0;
        provider.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(++attempts == 1 ?
            GmailFixture.Reply("{\"error\":{\"errors\":[{\"reason\":\"" + reason + "\"}]}}", HttpStatusCode.Forbidden) : null);
        using GmailApiClient api = Client(provider, clock);

        // Act
        Task<JsonDocument> reading = api.RequestAsync(HttpMethod.Get, "messages/one?format=metadata", null, 65536, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(59));

        // Assert
        Assert.That(reading.IsCompleted, Is.False);
        Assert.That(attempts, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        using JsonDocument result = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.RootElement.GetProperty("id").GetString(), Is.EqualTo("one"));
        Assert.That(attempts, Is.EqualTo(2));
    }

    [Test]
    public async Task RequestAsync_WhenShutdownCancelsQuotaWait_DoesNotDispatchAnotherRequest()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        using GmailApiClient api = Client(provider, clock);
        using JsonDocument first = await api.RequestAsync(HttpMethod.Get, "messages/one?format=metadata", null, 65536, CancellationToken.None);
        using CancellationTokenSource stopping = new CancellationTokenSource();
        Task<JsonDocument> reading = api.RequestAsync(HttpMethod.Get, "messages/two?format=metadata", null, 65536, stopping.Token);

        // Act
        await stopping.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.That(provider.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RequestAsync_WhenSendIsRateLimited_DoesNotRetryTheEffect()
    {
        // Arrange
        await using GmailFixture provider = new GmailFixture();
        UpdatePolicyClock clock = new UpdatePolicyClock();
        provider.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(GmailFixture.Reply(
            "{\"error\":{\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}",
            HttpStatusCode.Forbidden
        ));
        using GmailApiClient api = Client(provider, clock);

        // Act
        GmailApiClient.RequestFailure? failure = await Assert.ThrowsAsync<GmailApiClient.RequestFailure>(async () =>
            await api.RequestAsync(HttpMethod.Post, "messages/send", new { raw = "synthetic" }, 65536, CancellationToken.None));

        // Assert
        Assert.That(failure!.Failure, Is.EqualTo(Failure.Transient));
        Assert.That(provider.Requests, Has.Count.EqualTo(1));
    }

    private static GmailApiClient Client(GmailFixture provider, TimeProvider clock)
    {
        IAccountAuthenticator authentication = Substitute.For<IAccountAuthenticator>();
        authentication.AcquireAsync(false, Arg.Any<CancellationToken>()).Returns(Outcome<AccessToken>.Success(
            new AccessToken(EngineFixture.Account, GmailFixture.Binding.Address, "synthetic-access", clock.GetUtcNow().AddHours(1))
        ));
        return new GmailApiClient(EngineFixture.Account, GmailFixture.Binding, authentication, provider.Http(), clock, true);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Postfold.Core;
using Postfold.Storage;

namespace Postfold.Engine.Tests;

public sealed class WorkloadTests
{
    [Test]
    [Category("Workload")]
    public async Task SearchAsync_WhenOneHundredThousandMessagesAreCached_ReturnsBoundedStableAccountPages()
    {
        // Arrange
        string directory = EngineFixture.DirectoryPath();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(directory);
        await profile.AddAccountAsync(new AccountId(3), "Synthetic three", CancellationToken.None);
        Stopwatch load = Stopwatch.StartNew();
        for (int owner = 1; owner <= 3; owner++)
        {
            AccountId account = new AccountId(owner);
            int count = owner == 3 ? 33334 : 33333;
            for (int offset = 0; offset < count; offset += 1000)
            {
                string folder = offset / 1000 % 3 == 0 ? "INBOX" : "Archive";
                ImmutableArray<MessageSnapshot>.Builder batch = ImmutableArray.CreateBuilder<MessageSnapshot>();
                for (int index = offset; index < Math.Min(count, offset + 1000); index++)
                {
                    uint uid = (uint)index + 1;
                    batch.Add(EngineFixture.Message(uid, account: account) with
                    {
                        Subject = uid % 17 == 0 ? "測試 café uncommonneedle " + uid : "Résumé synthetic " + uid,
                        Size = 100 + uid % 10000,
                        Read = uid % 3 == 0,
                        Flagged = uid % 13 == 0,
                        Date = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index % 365)
                            .AddSeconds(index),
                        Provider = new ProviderIdentity(account, folder + ":7:" + uid,
                            new ImapIdentity(account, folder, 7, uid))
                    });
                }

                MetadataPage page = EngineFixture.Page(batch.ToImmutable(), account: account);
                await profile.CommitAsync(EngineFixture.Mailbox(account: account) with
                {
                    RemoteKey = folder,
                    Name = folder
                },
                    page with
                    {
                        Checkpoint = page.Checkpoint with
                        {
                            Scope = folder
                        },
                        FullEnumeration = false
                    },
                    CancellationToken.None);
            }

            Outcome<SearchPage> corpus = await profile.SearchAsync(new SearchRequest(account, false,
                "", null, false, 50), CancellationToken.None);
            foreach (MessageSnapshot message in corpus.Value!.Messages)
            {
                string body = "bodyneedle résumé 測試 " + new string('x', (int)(message.Size % 8192));
                using MemoryStream source = new MemoryStream(Encoding.UTF8.GetBytes(body));
                BlobReference blob = await profile.Blobs.WriteAsync(account, source, 16384, false, CancellationToken.None);
                ImmutableArray<AttachmentSnapshot> attachments = message.Flagged ?
                    [new AttachmentSnapshot("2", "synthetic résumé.pdf", "application/pdf", 65536)] : [];
                await profile.StoreBodyAsync(account, message.Id, blob,
                    new MimeSnapshot(message.Subject, message.Participants, message.MessageIdHeader,
                        body, null, attachments), CancellationToken.None);
            }
        }

        load.Stop();

        // Act
        List<double> samples = new List<double>();
        HashSet<MessageId> ids = new HashSet<MessageId>();
        string? token = null;
        for (int page = 0; page < 40; page++)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Outcome<SearchPage> result = await profile.SearchAsync(new SearchRequest(EngineFixture.Account,
                false, "résumé", null, false, 50, token), CancellationToken.None);
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
            foreach (MessageSnapshot message in result.Value!.Messages)
            {
                Assert.That(message.Account, Is.EqualTo(EngineFixture.Account));
                Assert.That(ids.Add(message.Id), Is.True);
            }

            token = result.Value.Continuation;
            Assert.That(result.Value.Messages, Has.Length.EqualTo(50));
        }

        Outcome<SearchPage> unicode = await profile.SearchAsync(new SearchRequest(null, true,
            "測試 uncommonneedle", null, false, 50), CancellationToken.None);
        List<double> bodySamples = new List<double>();
        for (int sample = 0; sample < 40; sample++)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Outcome<SearchPage> body = await profile.SearchAsync(new SearchRequest(EngineFixture.Account,
                false, "bodyneedle 測試", null, false, 50), CancellationToken.None);
            timer.Stop();
            bodySamples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.That(body.Value!.Messages, Has.Length.EqualTo(50));
        }

        string plan = await profile.ExplainSearchAsync(CancellationToken.None);
        double[] ordered = samples.Order().ToArray();
        await File.WriteAllTextAsync(Path.Combine(directory, "measurement.json"), JsonSerializer.Serialize(new
        {
            Seed = "Postfold-engine-v2",
            Messages = 100000,
            Accounts = 3,
            Configuration = "Release",
            Runtime = Environment.Version.ToString(),
            Os = Environment.OSVersion.ToString(),
            Processors = Environment.ProcessorCount,
            LoadMilliseconds = load.Elapsed.TotalMilliseconds,
            Cache = "Warm OS/data cache; 150 cached bodies with attachment metadata; Inbox/Archive; no renderer",
            SamplesMilliseconds = samples,
            BodySamplesMilliseconds = bodySamples,
            BodyP95Milliseconds = bodySamples.Order().ElementAt(37),
            MedianMilliseconds = ordered[ordered.Length / 2],
            P95Milliseconds = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
            QueryPlan = plan
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        TestContext.Out.WriteLine("Workload evidence: " + Path.Combine(directory, "measurement.json"));

        // Assert
        Assert.That(ids, Has.Count.EqualTo(2000));
        Assert.That(unicode.Value!.Messages, Has.Length.EqualTo(50));
        Assert.That(plan, Does.Contain("MessageDate"));
    }
}

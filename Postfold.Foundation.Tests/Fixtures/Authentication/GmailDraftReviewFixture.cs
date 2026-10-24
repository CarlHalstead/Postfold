using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Models.Authentication;
using Postfold.Engine.Tests.Fixtures;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Authentication;

internal static class GmailDraftReviewFixture
{
    internal static async Task<DesktopMailRuntime> OpenAsync(
        string directory,
        GmailFixture provider,
        bool allowNetwork = true
    )
    {
        DesktopMailRuntime runtime = (await DesktopMailRuntime.OpenAsync(
            directory,
            new EnginePolicy(allowNetwork, false, false),
            TimeProvider.System,
            new EngineLimits(2, 32, 1, 10, 1048576, 50, 0, 0, 1048576),
            CancellationToken.None
        )).Value!;
        try
        {
            runtime.ConfigureProviderClients(new ProviderClientConfiguration(
                GmailFixture.Binding.ClientId,
                GooglePolicyDecisionReference: "synthetic review only"
            ));
            runtime.GoogleHttpFactory = provider.Http;
            await runtime.RestoreAccountsAsync(Substitute.For<ICredentialStore>(), provider.Authentication, provider.Authentication, CancellationToken.None);
            if ((await runtime.Catalog.AccountsAsync(CancellationToken.None)).IsEmpty)
            {
                Assert.That((await runtime.ConnectProviderAsync(new ProviderSetupRequest("Google"), CancellationToken.None)).Failure, Is.EqualTo(Failure.None));
            }
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    internal static async Task<DraftSnapshot> HoldAsync(
        DesktopMailRuntime runtime,
        GmailFixture provider
    )
    {
        IMailApplication application = runtime.Application;
        Guid id = Guid.NewGuid();
        using MemoryStream attachment = new MemoryStream(Encoding.UTF8.GetBytes("protected attachment bytes"));
        ComposeAttachment imported = (await application.ImportAttachmentAsync(EngineFixture.Account, "evidence.txt", attachment, CancellationToken.None)).Value!;
        ComposeContent original = new ComposeContent(
            GmailFixture.Binding.Address,
            ["first@synthetic.invalid"],
            "review fixture",
            "original remote text",
            DateTimeOffset.UnixEpoch,
            "review@synthetic.invalid"
        )

        {
            Attachments = [imported]
        };
        Assert.That((await application.SaveDraftAsync(EngineFixture.Account, id, original, CancellationToken.None)).IsSuccess, Is.True);
        Assert.That(await application.PushDraftAsync(EngineFixture.Account, id, CancellationToken.None), Is.EqualTo(Failure.None));
        await application.SaveDraftAsync(EngineFixture.Account, id, original with { PlainText = "reviewed local text" }, CancellationToken.None);
        provider.ChangeDraftVersion("draft-1");
        Assert.That(await application.PushDraftAsync(EngineFixture.Account, id, CancellationToken.None), Is.EqualTo(Failure.NeedsReview));
        return (await runtime.Catalog.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Single(item => item.Id == id);
    }

    internal static void AssertSafeEffects(
        GmailFixture provider,
        int creates
    )
    {
        Assert.That(provider.DraftWrites, Is.EqualTo(creates));
        Assert.That(provider.Sends, Is.Zero);
        Assert.That(provider.Requests.Any(item => item.Method is "PUT" or "DELETE"), Is.False);
    }
}

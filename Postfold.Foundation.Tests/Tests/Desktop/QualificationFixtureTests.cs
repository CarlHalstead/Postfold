using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Diagnostics;
using Postfold.Desktop.Services;
using Postfold.Desktop.Stores;
using Postfold.Engine.Tests.Fixtures;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Desktop;

public sealed class QualificationFixtureTests
{
    [Test]
    public void Capture_WhenRunningOnWindows_IncludesCurrentProcessAndReadsPrivateMemoryAndCpu()
    {
        // Arrange
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Windows process-tree sampling requires the native Windows API.");
            return;
        }

        // Act
        ProcessTreeMeasurement.Snapshot sample = ProcessTreeMeasurement.Capture();

        // Assert
        Assert.That(sample.Complete, Is.True);
        Assert.That(sample.ProcessIds, Does.Contain(Environment.ProcessId));
        Assert.That(sample.PrivateBytes, Is.GreaterThan(0));
        Assert.That(sample.CpuSeconds, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task SeedReadingFixtureAsync_WhenNetworkIsDisabled_UsesRealCachedMimeAndSelectedAttachmentPorts()
    {
        // Arrange
        EngineLimits limits = new EngineLimits(2, 16, 1, 10, 1048576, 50, 0, 0, 10485760);
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(),
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;
        await host.SeedSyntheticAsync(3, CancellationToken.None);

        // Act
        await host.SeedReadingFixtureAsync(CancellationToken.None);
        MessageSnapshot message = (await host.Application.SearchAsync(new SearchRequest(
            new AccountId(1),
            false,
            "",
            null,
            false,
            1
        ), CancellationToken.None)).Value!.Messages[0];
        Outcome<MimeHtmlDocument> html = await host.Application.ReadHtmlAsync(message, false, CancellationToken.None);
        ImmutableArray<AttachmentSnapshot> attachments = (await host.Application.ReadAsync(
            message,
            false,
            CancellationToken.None
        )).Value!.Attachments;
        using MemoryStream destination = new MemoryStream();
        Failure saved = await host.Application.SaveAttachmentAsync(
            message,
            attachments[^1].Part,
            destination,
            CancellationToken.None
        );

        // Assert
        Assert.That(host.NetworkAllowed, Is.False);
        Assert.That(html.IsSuccess, Is.True);
        Assert.That(html.Value!.Html, Does.Contain("Authored synthetic HTML fixture"));
        Assert.That(html.Value.Images, Has.Length.EqualTo(1));
        Assert.That(PngImageNormalizer.Normalize(html.Value.Images[0].Bytes).IsSuccess, Is.True);
        Assert.That(attachments[^1].Name, Is.EqualTo("../../report.txt"));
        Assert.That(saved, Is.EqualTo(Failure.None));
        Assert.That(Encoding.UTF8.GetString(destination.ToArray()), Does.Contain("Authored synthetic attachment."));
    }

    [Test]
    public async Task SeedReadingFixtureAsync_WhenNetworkingIsEnabled_RefusesBeforeCreatingFixtureData()
    {
        // Arrange
        EngineLimits limits = new EngineLimits(2, 16, 1, 10, 1048576, 50, 0, 0, 10485760);
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(),
            new EnginePolicy(true, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;
        await

                // Act / Assert
                Assert.ThrowsAsync<InvalidOperationException>(() => host.SeedReadingFixtureAsync(CancellationToken.None));
        Assert.That(await host.Catalog.CachedMessageCountAsync(CancellationToken.None), Is.Zero);
    }

    [Test]
    public async Task SaveDraftAsync_WhenQualificationInjectsFailure_PreservesExistingDraftAndCanRecover()
    {
        // Arrange
        EngineLimits limits = new EngineLimits(2, 16, 1, 10, 1048576, 50, 0, 0, 10485760);
        await using DesktopMailRuntime host = (await DesktopMailRuntime.OpenAsync(
            EngineFixture.DirectoryPath(),
            new EnginePolicy(false, false, false),
            TimeProvider.System,
            limits,
            CancellationToken.None
        )).Value!;
        await host.SeedSyntheticAsync(3, CancellationToken.None);
        await host.RestoreAccountsAsync(new SessionCredentialStore(), CancellationToken.None);
        QualificationMailApplication application = new QualificationMailApplication(host.Application);
        AccountId account = new AccountId(1);
        Guid id = Guid.NewGuid();
        ComposeContent original = new ComposeContent(
            "sender@synthetic.invalid",
            [],
            "Original",
            "Original text",
            DateTimeOffset.UnixEpoch,
            "fixture@synthetic.invalid"
        );
        await application.SaveDraftAsync(account, id, original, CancellationToken.None);
        application.FailDraftSaves = true;

        // Act
        Outcome<DraftSnapshot> failed = await application.SaveDraftAsync(
            account,
            id,
            original with
            {
                PlainText = "Preserve these new edits"
            },
            CancellationToken.None
        );
        DraftSnapshot existing = (await application.Catalog.DraftsAsync(account, CancellationToken.None))[0];
        ComposeContent preserved = (await application.ReadDraftAsync(existing, CancellationToken.None)).Value!;
        application.FailDraftSaves = false;
        Outcome<DraftSnapshot> recovered = await application.SaveDraftAsync(
            account,
            id,
            original with
            {
                PlainText = "Recovered edits"
            },
            CancellationToken.None
        );

        // Assert
        Assert.That(failed.Failure, Is.EqualTo(Failure.Unavailable));
        Assert.That(preserved.PlainText, Is.EqualTo("Original text"));
        Assert.That(recovered.IsSuccess, Is.True);
        Assert.That(
            (await application.ReadDraftAsync(recovered.Value!, CancellationToken.None)).Value!.PlainText,
            Is.EqualTo("Recovered edits")
        );
        Assert.That(await application.ProcessOutboxAsync(account, CancellationToken.None), Is.EqualTo(Failure.Unavailable));
    }

    [Test]
    public async Task AddAsync_WhenSetupFixtureIsUsed_ReturnsOnlyAuthoredAccountRolesWithoutStoringCredentials()
    {
        // Arrange
        IClientCatalog catalog = Substitute.For<IClientCatalog>();
        IAccountSetup rolePort = Substitute.For<IAccountSetup>();
        MailboxSnapshot first = new MailboxSnapshot(
            new AccountId(1),
            new FolderId(1),
            "Sent",
            "Sent",
            MailboxRole.Sent,
            true,
            true,
            1
        );
        catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray.Create(
            first,
            first with
            {
                Account = new AccountId(2)
            }
        ));
        QualificationAccountSetup setup = new QualificationAccountSetup(catalog, rolePort)
        {
            Outgoing = Failure.TlsRejected
        };
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic", "synthetic");

        // Act
        AccountSetupResult result = await setup.AddAsync(new AccountSetupRequest(
            "Synthetic",
            endpoint,
            endpoint,
            "synthetic",
            "synthetic",
            false,
            null
        ), CancellationToken.None);

        // Assert
        Assert.That(result.Outgoing, Is.EqualTo(Failure.TlsRejected));
        Assert.That(result.Mailboxes, Is.EqualTo(new[] { first }));
        await rolePort.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await rolePort.DidNotReceiveWithAnyArgs().RestoreCredentialsAsync(default, default!, default!, default, default);
    }
}

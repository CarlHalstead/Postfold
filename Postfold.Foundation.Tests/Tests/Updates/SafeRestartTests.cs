using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services;
using Postfold.Desktop.Services.Updates;
using Postfold.Engine.Tests.Fakes;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.Protocols.Services;
using Postfold.Storage.Stores;
using Postfold.Sync.Services;
using Postfold.Sync.Services.Work;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class SafeRestartTests
{
    [AvaloniaTest]
    public async Task RestartAsync_WhenCloseRacesSessionAdmission_ObservesTheOwnerWithoutDuplicateShutdownOrNativeLaunch()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        lifetime.When(value => value.Shutdown(Arg.Any<int>())).Do(_ => stopped.TrySetResult());
        NativeRestartConfiguration native = new NativeRestartConfiguration(
            Path.Combine(fixture.Directory, "missing-broker.exe"),
            new string('0', 64),
            Path.Combine(fixture.Directory, "missing-helper.exe"),
            new string('0', 64),
            Path.Combine(fixture.Directory, "installation"),
            new string('0', 64)
        );
        DesktopSession session = fixture.CreateSession(lifetime, fixture.Directory + "-profiles", fixture.Clock, new Connection(), native);
        session.Window.Show();
        await session.Startup;
        ScheduledClientUpdates updates = (ScheduledClientUpdates)typeof(DesktopSession).GetField("_updates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        await updates.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(updates.Current.CanRestart, Is.True);

        // Act
        Task<UpdateStatus> restarting = updates.RestartAsync(CancellationToken.None);
        session.Window.Close();
        await restarting.WaitAsync(TimeSpan.FromSeconds(10));
        if (!stopped.Task.IsCompleted)
        {
            session.Window.Close();
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(updates.IsBusy, Is.False);
        lifetime.Received(1).Shutdown(Arg.Any<int>());
        Assert.That(Directory.GetFiles(fixture.Directory, "*.apply-*.log", SearchOption.AllDirectories), Is.Empty);
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        session.Window.Close();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RestartAsync_WhenRepeated_SharesOwnerAndExcludesDiscoveryUntilAcknowledged(bool cancel)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        await using SqliteProfile profile = await EngineFixture.OpenAsync(fixture.Directory + "-profile");
        await profile.SaveDesktopPreferencesAsync(DesktopPreferences.Default with
        {
            Updates = UpdatePreferences.Default with { DownloadsEnabled = false }
        }, CancellationToken.None);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<UpdateStatus> completion = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Restart restart = new Restart(entered, completion);
        await using ScheduledClientUpdates service = fixture.CreateScheduled(profile, new Connection(), fixture.Clock, restart);
        await service.StartAsync(CancellationToken.None);
        await service.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(10));
        service.Configure(UpdatePreferences.Default);
        await service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        await service.DownloadAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        Assert.That(service.Current.CanRestart, Is.True);

        // Act
        Task<UpdateStatus> first = service.RestartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<UpdateStatus> repeated = service.RestartAsync(CancellationToken.None);
        UpdateStatus competing = await service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        Task? acknowledgement = cancel ? service.CancelAsync() : null;

        // Assert
        try
        {
            Assert.That(repeated, Is.SameAs(first));
            Assert.That(service.IsBusy, Is.True);
            Assert.That(competing.Failure, Is.EqualTo(Failure.PolicyBlocked));
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.RestartPreparing));
            Assert.That(service.IsCancelling, Is.EqualTo(cancel));
            if (cancel)
            {
                Assert.That(acknowledgement, Is.SameAs(first));
                Assert.That(acknowledgement!.IsCompleted, Is.False);
            }
        }
        finally
        {
            completion.TrySetResult(new UpdateStatus(UpdateStage.RestartBlocked, Failure: Failure.NeedsReview)
            {
                CanRestart = true
            });
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.That(service.IsBusy, Is.False);
        Assert.That(service.Current.Stage, Is.EqualTo(cancel ? UpdateStage.Cancelled : UpdateStage.RestartBlocked));
        Assert.That(restart.Calls, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Shutdown_WhenMultipleDraftsAreDirty_SavesActualProfileAndPreservesUncertainAndQueuedWork()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        string directory = Path.Combine(profiles, "default");
        Guid uncertain;
        Guid queued;
        await using (SqliteProfile seed = await EngineFixture.OpenAsync(directory))
        {
            MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic@example.invalid", "missing");
            await seed.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
            FrozenSubmission first = EngineFixture.Submission(await EngineFixture.BlobAsync(seed, "preserved uncertain MIME"));
            uncertain = first.Id;
            await seed.QueueAsync(first, CancellationToken.None);
            OutboxEntry claim = (await seed.ClaimAsync(first.Id, first.Account, CancellationToken.None))!;
            await seed.FinishAsync(claim, new SubmissionOutcome(
                SubmissionState.Uncertain,
                SubmissionPhase.AwaitingAcknowledgement,
                first.Recipients.Select(address => new RecipientEvidence(address, RecipientState.Uncertain, null)).ToImmutableArray(),
                Failure.NeedsReview
            ), CancellationToken.None);
            FrozenSubmission second = EngineFixture.Submission(await EngineFixture.BlobAsync(seed, "preserved queued MIME"));
            queued = second.Id;
            await seed.QueueAsync(second, CancellationToken.None);
        }
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        DesktopSession session = new DesktopSession(lifetime, profilesDirectory: profiles);
        TaskCompletionSource stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.When(value => value.Shutdown(Arg.Any<int>())).Do(_ => stopped.TrySetResult());
        session.Window.Show();
        await session.Startup;
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        for (int index = 0; index < 2; index++)
        {
            session.Window.OpenCompose(activation: new ComposeContent(
                "synthetic@example.invalid",
                [],
                "Unsaved synthetic " + index,
                "Durable body " + index,
                DateTimeOffset.UnixEpoch,
                "synthetic@example.invalid"
            ));
        }
        Guid[] drafts = session.Window.OwnedWindows.OfType<ComposeWindow>().Select(window => window.DraftId).ToArray();

        // Act
        session.Window.Close();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(session.Window.IsEnabled, Is.False);
        using (FileStream lease = new FileStream(Path.Combine(directory, "profile.db.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.That(drafts, Has.Length.EqualTo(2));
        }
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(directory);
        Assert.That((await reopened.DraftsAsync(EngineFixture.Account, CancellationToken.None)).Select(draft => draft.Id), Is.EquivalentTo(drafts));
        ImmutableArray<OutboxEntry> entries = await reopened.OutboxAsync(EngineFixture.Account, CancellationToken.None);
        Assert.That(entries.Single(entry => entry.Submission.Id == uncertain).State, Is.EqualTo(SubmissionState.Uncertain));
        Assert.That(entries.Single(entry => entry.Submission.Id == queued).State, Is.EqualTo(SubmissionState.Queued));
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        session.Window.Close();
    }

    [AvaloniaTest]
    public async Task Shutdown_WhenSubmittingRowIsOutsidePresentationPage_BlocksAndRetainsLeaseAndUsableWindow()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        DesktopMailRuntime host = Host(session);
        SqliteProfile profile = (SqliteProfile)host.Catalog;
        await profile.AddAccountAsync(EngineFixture.Account, "Synthetic", CancellationToken.None);
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "retained submission"));
        await profile.QueueAsync(frozen, CancellationToken.None);
        OutboxEntry claim = (await profile.ClaimAsync(frozen.Id, frozen.Account, CancellationToken.None))!;
        for (int index = 0; index < 201; index++)
        {
            await profile.QueueAsync(frozen with { Id = Guid.NewGuid() }, CancellationToken.None);
        }

        // Act
        Failure result = await session.PrepareUpdateShutdownAsync(CancellationToken.None);

        // Assert
        try
        {
            Assert.That(result, Is.EqualTo(Failure.NeedsReview));
            Assert.That(session.Window.IsEnabled, Is.True);
            Assert.That(await profile.HasActiveSubmissionsAsync(CancellationToken.None), Is.True);
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(profiles, "default", "profile.db.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            await profile.FinishAsync(claim, new SubmissionOutcome(
                SubmissionState.Uncertain,
                SubmissionPhase.AwaitingAcknowledgement,
                frozen.Recipients.Select(address => new RecipientEvidence(address, RecipientState.Uncertain, null)).ToImmutableArray(),
                Failure.NeedsReview
            ), CancellationToken.None);
            await session.PrepareUpdateShutdownAsync(CancellationToken.None);
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("connected", false)]
    [TestCase("awaiting-acknowledgement", false)]
    [TestCase("awaiting-acknowledgement", true)]
    [TestCase("accepted", false)]
    public async Task Shutdown_WhenSmtpIsAtControlledBoundary_ObservesAcknowledgementAndNeverResubmits(string boundary, bool loseAcknowledgement)
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        DesktopMailRuntime host = Host(session);
        SqliteProfile profile = (SqliteProfile)host.Catalog;
        await profile.AddAccountAsync(EngineFixture.Account, "Synthetic", CancellationToken.None);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using LocalMailServer server = new LocalMailServer(imap: false)
        {
            LoseAcknowledgement = loseAcknowledgement,
            SmtpBoundary = async phase =>
            {
                if (phase == boundary)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
        };
        InMemoryCredentialStore credentials = new InMemoryCredentialStore();
        await credentials.WriteAsync("synthetic", new Credential("synthetic", false), CancellationToken.None);
        SmtpSubmitter submitter = new SmtpSubmitter(
            EngineFixture.Account,
            new MailEndpoint("localhost", server.Port, TlsMode.Implicit, "synthetic", "synthetic"),
            credentials
        )
        {
            FixtureAuthority = server.Authority
        };
        FrozenSubmission frozen = EngineFixture.Submission(await EngineFixture.BlobAsync(profile,
            "From: sender@synthetic.invalid\r\nTo: first@synthetic.invalid\r\nSubject: Synthetic\r\n\r\nPreserve this MIME.\r\n"));
        await profile.QueueAsync(frozen, CancellationToken.None);
        ISentCopyStore sentCopy = Substitute.For<ISentCopyStore>();
        sentCopy.SaveAsync(Arg.Any<FrozenSubmission>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(Failure.NeedsReview);
        AccountScheduler scheduler = (AccountScheduler)typeof(DesktopMailRuntime).GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        Task<Failure> submission = scheduler.EnqueueAsync(EngineFixture.Account,
            new SubmissionWork(
                new OutboxWork(EngineFixture.Account, profile, profile.Blobs, submitter, frozen.Id),
                new SentCopyWork(frozen.Id, EngineFixture.Account, profile, profile.Blobs, sentCopy)
            ), WorkPriority.Interactive, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        Task<Failure> shutdown = session.PrepareUpdateShutdownAsync(CancellationToken.None);
        bool waiting = !shutdown.IsCompleted;
        session.Window.Close();
        Failure repeated = await session.PrepareUpdateShutdownAsync(CancellationToken.None);
        release.TrySetResult();
        await submission.WaitAsync(TimeSpan.FromSeconds(10));
        Failure result = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(waiting, Is.True);
        Assert.That(repeated, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(result, Is.EqualTo(Failure.None));
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(Path.Combine(profiles, "default"));
        OutboxEntry saved = (await reopened.OutboxAsync(EngineFixture.Account, CancellationToken.None)).Single();
        Assert.That(saved.State, Is.EqualTo(loseAcknowledgement ? SubmissionState.Uncertain : SubmissionState.SentCopyPending));
        await new OutboxWork(EngineFixture.Account, reopened, reopened.Blobs, submitter, frozen.Id).RunAsync(CancellationToken.None);
        Assert.That(server.SubmissionCount, Is.EqualTo(1));
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        session.Window.Close();
    }

    [AvaloniaTest]
    public async Task Shutdown_WhenDrainIsCancelled_RetainsLeaseAndResumesOnlyNewAdmissions()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        DesktopMailRuntime host = Host(session);
        AccountScheduler scheduler = (AccountScheduler)typeof(DesktopMailRuntime).GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        IAccountWork active = Substitute.For<IAccountWork>();
        active.RunAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            observed = call.Arg<CancellationToken>();
            entered.TrySetResult();
            await release.Task;
            return Failure.None;
        });
        IAccountWork queued = Substitute.For<IAccountWork>();
        queued.RunAsync(Arg.Any<CancellationToken>()).Returns(Failure.None);
        Task<Failure> running = scheduler.EnqueueAsync(EngineFixture.Account, active, WorkPriority.Interactive, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<Failure> discarded = scheduler.EnqueueAsync(EngineFixture.Account, queued, WorkPriority.Interactive, CancellationToken.None);
        using CancellationTokenSource cancellation = new CancellationTokenSource();

        // Act
        Task<Failure> shutdown = session.PrepareUpdateShutdownAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await shutdown;
            Assert.Fail("Cancellation must be acknowledged before resuming admissions.");
        }
        catch (OperationCanceledException)
        {
        }

        // Assert
        try
        {
            Assert.That(session.Window.IsEnabled, Is.True);
            Assert.That(observed.IsCancellationRequested, Is.False);
            Assert.That(await discarded, Is.EqualTo(Failure.PolicyBlocked));
            await queued.DidNotReceive().RunAsync(Arg.Any<CancellationToken>());
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(profiles, "default", "profile.db.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(await scheduler.EnqueueAsync(EngineFixture.Account, queued, WorkPriority.Interactive, CancellationToken.None), Is.EqualTo(Failure.None));
            await session.PrepareUpdateShutdownAsync(CancellationToken.None);
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
        }
    }

    [AvaloniaTest]
    public async Task Shutdown_WhenSettingsOwnsAStoreWrite_DrainsBeforeReleasingTheRealProfileLease()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        SqliteProfile profile = (SqliteProfile)Host(session).Catalog;
        SemaphoreSlim writer = (SemaphoreSlim)typeof(SqliteProfile).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(profile)!;
        await writer.WaitAsync();
        Task save = profile.SaveDesktopPreferencesAsync(DesktopPreferences.Default with
        {
            Updates = UpdatePreferences.Default with { CheckIntervalHours = 168 }
        }, CancellationToken.None);
        typeof(MainWindow).Assembly.GetType("Postfold.UI.Controls.FormControls")!
            .GetMethod("Track", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [session.Window, save]);

        // Act
        Task<Failure> shutdown = session.PrepareUpdateShutdownAsync(CancellationToken.None);
        bool waiting = !shutdown.IsCompleted;
        bool leaseHeld;
        try
        {
            using FileStream lease = new FileStream(Path.Combine(profiles, "default", "profile.db.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            leaseHeld = false;
        }
        catch (IOException)
        {
            leaseHeld = true;
        }
        writer.Release();
        Failure result = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(waiting, Is.True);
        Assert.That(leaseHeld, Is.True);
        Assert.That(result, Is.EqualTo(Failure.None));
        await using SqliteProfile reopened = await EngineFixture.OpenAsync(Path.Combine(profiles, "default"));
        Assert.That((await reopened.ReadDesktopPreferencesAsync(CancellationToken.None)).Updates.CheckIntervalHours, Is.EqualTo(168));
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        session.Window.Close();
    }

    [AvaloniaTest]
    public async Task Shutdown_WhenAnotherSessionOwnsTheProfile_RefusesAdmissionAndPreservesTheLease()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        await using SqliteProfile owner = await EngineFixture.OpenAsync(Path.Combine(profiles, "default"));
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;

        // Act
        Failure result = await session.PrepareUpdateShutdownAsync(CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(session.Window.IsEnabled, Is.True);
        Assert.That(await owner.HasActiveSubmissionsAsync(CancellationToken.None), Is.False);
        session.Window.Close();
    }

    [AvaloniaTest]
    [Platform("Win")]
    public async Task Shutdown_WhenRendererAndPreviewCompleteLate_ObservesBothBeforeReleasingProfileAndIgnoresTheirResults()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        DesktopSession session = new DesktopSession(Substitute.For<IClassicDesktopStyleApplicationLifetime>(), profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        MainWindowViewModel original = (MainWindowViewModel)session.Window.DataContext!;
        FieldInfo rendererField = typeof(DesktopSession).GetField("_html", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((WindowsHtmlReadingService)rendererField.GetValue(session)!).DisposeAsync();
        IMailApplication application = Substitute.For<IMailApplication>();
        TaskCompletionSource<Outcome<MimeSnapshot>> preview = new TaskCompletionSource<Outcome<MimeSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Outcome<MimeHtmlDocument>> html = new TaskCompletionSource<Outcome<MimeHtmlDocument>>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource previewCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource htmlCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MessageSnapshot message = EngineFixture.Message(1);
        application.ReadAsync(message, true, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Register(() => previewCancelled.TrySetResult());
            return preview.Task;
        });
        application.ReadHtmlAsync(message, false, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Register(() => htmlCancelled.TrySetResult());
            return html.Task;
        });
        MainWindowViewModel model = new MainWindowViewModel(application);
        session.Window.DataContext = model;
        model.Selected = message;
        string previousBody = model.Body;
        WindowsHtmlReadingService renderer = new WindowsHtmlReadingService(session.Window, application,
            Substitute.For<IRemoteImageFetcher>(), Path.Combine(profiles, "renderer"), qualified: true);
        rendererField.SetValue(session, renderer);
        Task<Failure> reading = renderer.ReadAsync(message, false, CancellationToken.None);

        // Act
        Task<Failure> shutdown = session.PrepareUpdateShutdownAsync(CancellationToken.None);
        await htmlCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool waitedForRenderer = !shutdown.IsCompleted;
        html.TrySetResult(Outcome<MimeHtmlDocument>.Fail(Failure.Unavailable));
        await reading;
        await previewCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool waitedForPreview = !shutdown.IsCompleted;
        preview.TrySetResult(Outcome<MimeSnapshot>.Success(new MimeSnapshot("Late", "", null, "Do not publish this late preview", null, [])));
        Failure result = await shutdown.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(waitedForRenderer, Is.True);
        Assert.That(waitedForPreview, Is.True);
        Assert.That(result, Is.EqualTo(Failure.None));
        Assert.That(model.Body, Is.EqualTo(previousBody));
        using FileStream lease = new FileStream(Path.Combine(profiles, "default", "profile.db.lease"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        session.Window.Close();
        await original.DisposeAsync();
    }

    private static DesktopMailRuntime Host(DesktopSession session)
    {
        return (DesktopMailRuntime)typeof(DesktopSession).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    }

    private sealed class Connection : IUpdateConnection
    {
        public UpdateConnection Current => UpdateConnection.Unmetered;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class Restart(TaskCompletionSource entered, TaskCompletionSource<UpdateStatus> completion) : IUpdateRestart
    {
        internal int Calls { get; private set; }

        public Task<UpdateStatus> RestartAsync(VerifiedUpdateSource release, CancellationToken cancellationToken)
        {
            Calls++;
            entered.TrySetResult();
            return completion.Task;
        }
    }

    private sealed class SubmissionWork(OutboxWork submission, SentCopyWork copy) : IAccountWork
    {
        public async Task<Failure> RunAsync(CancellationToken cancellationToken)
        {
            Failure result = await submission.RunAsync(cancellationToken);
            return result == Failure.None ? await copy.RunAsync(cancellationToken) : result;
        }
    }
}

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Search;
using Postfold.Desktop.Composition;
using Postfold.Engine.Tests.Fixtures;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class UpdateShutdownTests
{
    [AvaloniaTest]
    public async Task PrepareUpdateShutdownAsync_WhenProfileIsAdmitted_ClosesOwnedWorkAndReleasesProfileLeaseBeforeReady()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        DesktopSession session = new DesktopSession(lifetime, profilesDirectory: profiles);
        MainWindowCleanup cleanup = new MainWindowCleanup(session.Window);
        using (cleanup)
        {
            session.Window.Show();
            await session.Startup;
            string database = Path.Combine(profiles, "default", "profile.db");

            // Act
            Failure ready = await session.PrepareUpdateShutdownAsync(CancellationToken.None);

            // Assert
            Assert.That(ready, Is.EqualTo(Failure.None), ((MainWindowViewModel)session.Window.DataContext!).Status);
            Assert.That(session.Window.IsEnabled, Is.False);
            Assert.That(File.Exists(database), Is.True);
            using FileStream exclusive = new FileStream(database + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.That(await session.PrepareUpdateShutdownAsync(CancellationToken.None), Is.EqualTo(Failure.PolicyBlocked));
        }
    }

    [AvaloniaTest]
    public async Task PrepareUpdateShutdownAsync_WhenDraftCannotSave_PreservesItsWindowAndBlocksRestart()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        DesktopSession session = new DesktopSession(lifetime, profilesDirectory: profiles);
        session.Window.Show();
        await session.Startup;
        foreach (Window child in session.Window.OwnedWindows.ToArray())
        {
            child.Close();
        }
        MainWindowViewModel originalModel = (MainWindowViewModel)session.Window.DataContext!;
        IMailApplication application = Substitute.For<IMailApplication>();
        application.SaveDraftAsync(Arg.Any<AccountId>(), Arg.Any<Guid>(), Arg.Any<ComposeContent>(), Arg.Any<CancellationToken>())
            .Returns(Outcome<DraftSnapshot>.Fail(Failure.Unavailable));
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic@example.invalid", "synthetic");
        MainWindowViewModel model = new MainWindowViewModel(application);
        model.Accounts.Add(new AccountConfiguration(new AccountId(1), "Synthetic", endpoint, endpoint));
        application.Catalog.AccountsAsync(Arg.Any<CancellationToken>()).Returns(model.Accounts.ToImmutableArray());
        application.Catalog.MailboxesAsync(Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxSnapshot>.Empty);
        application.Catalog.MailboxUnreadCountsAsync(Arg.Any<AccountId>(), Arg.Any<CancellationToken>()).Returns(ImmutableArray<MailboxUnreadCount>.Empty);
        application.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>()).Returns(Outcome<SearchPage>.Success(new SearchPage([], null)));
        session.Window.DataContext = model;
        session.Window.Configure(application, null);
        session.Window.OpenCompose(activation: new ComposeContent(
            "synthetic@example.invalid",
            [],
            "Synthetic unsaved work",
            "Retain this synthetic text",
            DateTimeOffset.UnixEpoch,
            "synthetic@example.invalid"
        ));
        ComposeWindow compose = session.Window.OwnedWindows.OfType<ComposeWindow>().Single();
        try
        {
            // Act
            Failure blocked = await session.PrepareUpdateShutdownAsync(CancellationToken.None);

            // Assert
            Assert.That(blocked, Is.EqualTo(Failure.Unavailable));
            Assert.That(compose.IsVisible, Is.True);
            Assert.That(compose.IsEnabled, Is.True);
            Assert.That(session.Window.IsEnabled, Is.True);
            Assert.That(model.Status, Is.EqualTo(Strings.Get("UpdateShutdownDraftBlocked")));
        }
        finally
        {
            // This failure-only fake owns no stored draft; finish only the synthetic window after assertions.
            compose.ConfirmShutdown();
            await session.PrepareUpdateShutdownAsync(CancellationToken.None);
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
            await originalModel.DisposeAsync();
        }
    }

    [AvaloniaTest]
    public async Task PrepareUpdateShutdownAsync_WhenAdmissionWasRefused_BlocksWithoutModifyingOccupiedProfile()
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        string occupied = Path.Combine(profiles, "default");
        Directory.CreateDirectory(occupied);
        string original = Path.Combine(occupied, "synthetic-protected-work");
        await File.WriteAllTextAsync(original, "Preserved synthetic work");
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        DesktopSession session = new DesktopSession(lifetime, profilesDirectory: profiles);
        using MainWindowCleanup cleanup = new MainWindowCleanup(session.Window);
        session.Window.Show();
        await session.Startup;

        // Act
        Failure blocked = await session.PrepareUpdateShutdownAsync(CancellationToken.None);

        // Assert
        Assert.That(blocked, Is.EqualTo(Failure.PolicyBlocked));
        Assert.That(await File.ReadAllTextAsync(original), Is.EqualTo("Preserved synthetic work"));
        Assert.That(File.Exists(Path.Combine(occupied, "profile.db")), Is.False);
        Assert.That(session.Window.IsEnabled, Is.True);
    }

    private sealed class MainWindowCleanup(Window window) : IDisposable
    {
        public void Dispose()
        {
            foreach (Window child in window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            window.Close();
        }
    }
}

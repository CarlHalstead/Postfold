using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services.Updates;
using Postfold.Desktop.Stores;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class AuthenticatedClientUpdatesTests
{
    [AvaloniaTest]
    public async Task Settings_WhenSignedReleaseIsDownloaded_ReopensDurableTrustAndReverifiesCache()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using (AuthenticatedClientUpdates service = fixture.Create())
        {
            await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);
            Assert.That(fixture.Requests, Is.Empty);
            Assert.That(System.IO.Directory.Exists(fixture.Directory), Is.False);

            // Act
            await ui.ClickAsync("CheckForUpdates");
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Available));
            Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.True);
            await ui.ClickAsync("DownloadUpdate");

            // Assert
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Ready));
            Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
            Assert.That(File.ReadAllBytes(Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName)), Is.EqualTo(fixture.Signer.Package));
        }
        using AuthenticatedClientUpdates reopened = fixture.Create();
        await using SettingsHarness reopenedUi = await SettingsHarness.OpenAsync(reopened);
        await reopenedUi.ClickAsync("CheckForUpdates");
        int cachedRequests = fixture.Requests.Count;
        await reopenedUi.ClickAsync("DownloadUpdate");
        Assert.That(reopened.Current.Stage, Is.EqualTo(UpdateStage.Ready));
        Assert.That(fixture.Requests.Count, Is.EqualTo(cachedRequests));
        // Reopen Settings to make another explicit check; no automatic download follows a check.
        await reopenedUi.DisposeAsync();
        await using SettingsHarness corruptUi = await SettingsHarness.OpenAsync(reopened);
        await corruptUi.ClickAsync("CheckForUpdates");
        File.WriteAllBytes(Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName), [1, 2, 3]);
        int before = fixture.Requests.Count;
        await corruptUi.ClickAsync("DownloadUpdate");
        Assert.That(reopened.Current.Failure, Is.EqualTo(Failure.Corrupt));
        Assert.That(fixture.Requests.Count, Is.EqualTo(before));
        Assert.That(corruptUi.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    [TestCase("current")]
    [TestCase("signature")]
    [TestCase("feed")]
    [TestCase("replay")]
    [TestCase("format")]
    public async Task Settings_WhenReleaseMatchesInstalledVersion_ReportsCurrentOnlyAfterAuthenticationAndDurableAcceptance(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        if (fault == "replay")
        {
            using AuthenticatedClientUpdates earlier = fixture.Create();
            Assert.That((await earlier.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Stage, Is.EqualTo(UpdateStage.Available));
            byte[] changed = fixture.Signer.Manifest(change: document => document["sourceCommit"] = new string('b', 40));
            fixture.Documents["Postfold.win-x64-stable.manifest.json"] = changed;
            fixture.Documents["Postfold.win-x64-stable.manifest.sig"] = fixture.Signer.Sign(changed);
        }
        if (fault is "signature" or "feed")
        {
            string name = fault == "signature" ? "Postfold.win-x64-stable.manifest.sig" : "releases.win-x64-stable.json";
            fixture.Documents[name] = [.. fixture.Documents[name], 32];
        }
        using AuthenticatedClientUpdates service = fixture.Create(version: "1.1.0", format: fault == "format" ? 3 : 2);
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(fault == "current" ? UpdateStage.Current : UpdateStage.Failed));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        Assert.That(fixture.Requests, Does.Not.Contain(ClientUpdateFixture.BundleName));
        if (fault == "current")
        {
            Assert.That(service.Current.Failure, Is.EqualTo(Failure.None));
            Assert.That(ui.Button("CheckForUpdates").IsEnabled, Is.True);
            using AuthenticatedClientUpdates reopened = fixture.Create(version: "1.1.0");
            Assert.That((await reopened.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Stage, Is.EqualTo(UpdateStage.Current));
        }
    }

    [AvaloniaTest]
    [TestCase("manifest")]
    [TestCase("signature")]
    [TestCase("feed")]
    [TestCase("product")]
    [TestCase("rid")]
    [TestCase("channel")]
    [TestCase("minimumApp")]
    [TestCase("format")]
    [TestCase("migration")]
    [TestCase("missing")]
    [TestCase("oversize")]
    public async Task Settings_WhenDiscoveryFailsTrustOrCompatibility_NeverEnablesDownloadOrRestart(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        byte[] manifest = fixture.Signer.Manifest(change: document =>
        {
            switch (fault)
            {
                case "product":
                    document["product"] = "Other";
                    break;
                case "rid":
                    document["rid"] = "linux-x64";
                    break;
                case "channel":
                    document["channel"] = "beta";
                    break;
                case "minimumApp":
                    document["minimumApp"] = "1.1.0";
                    break;
            }
        });
        fixture.Documents["Postfold.win-x64-stable.manifest.json"] = manifest;
        fixture.Documents["Postfold.win-x64-stable.manifest.sig"] = fixture.Signer.Sign(manifest);
        if (fault is "manifest" or "feed" or "signature")
        {
            string name = fault == "feed" ? "releases.win-x64-stable.json" : "Postfold.win-x64-stable.manifest." + (fault == "signature" ? "sig" : "json");
            fixture.Documents[name] = [.. fixture.Documents[name], 32];
        }
        if (fault == "missing")
        {
            fixture.Documents.Remove("releases.win-x64-stable.json");
        }
        if (fault == "oversize")
        {
            fixture.Response = (name, _) =>
            {
                HttpResponseMessage response = fixture.Respond(name);
                if (name.EndsWith("manifest.json", StringComparison.Ordinal))
                {
                    response.Content = new ByteArrayContent(new byte[65537]);
                    response.Content.Headers.ContentLength = 1;
                }
                return Task.FromResult(response);
            };
        }
        using AuthenticatedClientUpdates service = fixture.Create(
            version: "1.0.0",
            format: fault == "format" ? 3 : 2,
            migration: fault == "migration" ? UpdateFixture.TargetMigration : UpdateFixture.InitialMigration
        );
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        UpdateStatus denied = await service.DownloadAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        Assert.That(denied.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(fixture.Requests, Does.Not.Contain(ClientUpdateFixture.BundleName));
    }

    [AvaloniaTest]
    [TestCase("wrong-key")]
    [TestCase("skipped-epoch")]
    [TestCase("wrong-activation")]
    [TestCase("missing-activation")]
    [TestCase("valid-chain")]
    public async Task Settings_WhenKeyChanges_RequiresEverySignedTransitionAndExactActivation(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using UpdateFixture successor = new UpdateFixture();
        byte[] activation = successor.Manifest();
        byte[] transition = fixture.Signer.Transition(successor, activation, epoch: fault == "skipped-epoch" ? 2 : 1, sequence: 1, version: "1.1.0");
        string transitionName = "transition-" + fixture.Signer.KeyId;
        string activationName = "activation-" + UpdateFixture.Digest(activation);
        fixture.Documents["Postfold.win-x64-stable.manifest.json"] = activation;
        fixture.Documents["Postfold.win-x64-stable.manifest.sig"] = successor.Sign(activation);
        if (fault != "wrong-key")
        {
            fixture.Documents[transitionName + ".json"] = transition;
            fixture.Documents[transitionName + ".sig"] = fixture.Signer.Sign(transition);
        }
        if (fault != "missing-activation")
        {
            byte[] supplied = fault == "wrong-activation" ? successor.Manifest(sequence: 2) : activation;
            fixture.Documents[activationName + ".json"] = supplied;
            fixture.Documents[activationName + ".sig"] = successor.Sign(supplied);
        }
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(fault == "valid-chain" ? UpdateStage.Available : UpdateStage.Failed));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.EqualTo(fault == "valid-chain"));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(fixture.Directory, "trust.journal"), fixture.Signer.PublicKey, "stable").Value!;
        Assert.That(trust.State.Sequence, Is.EqualTo(fault == "valid-chain" ? 1 : 0));
    }

    [AvaloniaTest]
    [TestCase("older")]
    [TestCase("equivocation")]
    [TestCase("corrupt")]
    [TestCase("missing-head")]
    [TestCase("missing-journal")]
    [TestCase("missing-both")]
    public async Task Settings_WhenDurableStateRejectsRelease_DoesNotResetTrustOrUseStaleSelection(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);
        await ui.ClickAsync("CheckForUpdates");
        string journal = Path.Combine(fixture.Directory, "trust.journal");
        if (fault is "older" or "equivocation")
        {
            using (UpdateTrustStore trust = UpdateTrustStore.Open(journal, fixture.Signer.PublicKey, "stable").Value!)
            {
                byte[] later = fixture.Signer.Manifest(sequence: 2, version: "1.2.0");
                Assert.That(trust.Accept(later, fixture.Signer.Sign(later)).IsSuccess, Is.True);
            }
            if (fault == "equivocation")
            {
                byte[] changed = fixture.Signer.Manifest(sequence: 2);
                fixture.Documents["Postfold.win-x64-stable.manifest.json"] = changed;
                fixture.Documents["Postfold.win-x64-stable.manifest.sig"] = fixture.Signer.Sign(changed);
            }
        }
        else if (fault == "corrupt")
        {
            File.WriteAllBytes(journal, [1, 2, 3]);
        }
        else if (fault == "missing-both")
        {
            System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory, "packages"));
            File.WriteAllBytes(Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName), fixture.Signer.Package);
            File.Move(journal, fixture.Directory + ".retained-journal");
            File.Move(journal + ".head", fixture.Directory + ".retained-head");
        }
        else
        {
            // Move the exact evidence aside rather than erasing it.
            string path = fault == "missing-head" ? journal + ".head" : journal;
            File.Move(path, path + ".retained");
        }

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    [TestCase("package")]
    [TestCase("cache")]
    [TestCase("missing")]
    [TestCase("overlong")]
    [TestCase("short")]
    public async Task Settings_WhenPackageBytesAreInvalid_NeverPromotesOrOffersRestart(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);
        await ui.ClickAsync("CheckForUpdates");
        string final = Path.Combine(fixture.Directory, "packages", ClientUpdateFixture.PackageName);
        if (fault == "cache")
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.WriteAllBytes(final, [9]);
        }
        else if (fault == "missing")
        {
            fixture.Documents.Remove(ClientUpdateFixture.PackageName);
        }
        else
        {
            byte[] bytes = fixture.Signer.Package.ToArray();
            bytes[0] ^= 1;
            fixture.Documents[ClientUpdateFixture.PackageName] = fault switch
            {
                "overlong" => [.. bytes, 1],
                "short" => bytes[..^1],
                _ => bytes
            };
        }

        // Act
        await ui.ClickAsync("DownloadUpdate");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        Assert.That(File.Exists(final), Is.EqualTo(fault == "cache"));
    }

    [AvaloniaTest]
    [TestCase("offline")]
    [TestCase("tls")]
    [TestCase("redirect")]
    [TestCase("rate-limit")]
    public async Task Settings_WhenTransportFails_ReportsFailureAndRetriesThroughRealTransport(string fault)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        fixture.Response = (_, _) =>
        {
            if (fault is "offline" or "tls")
            {
                throw new HttpRequestException(fault == "tls" ? HttpRequestError.SecureConnectionError : HttpRequestError.ConnectionError);
            }
            HttpResponseMessage response = new HttpResponseMessage(fault == "redirect" ? HttpStatusCode.Found : HttpStatusCode.TooManyRequests);
            response.Headers.Location = new Uri("http://other.invalid/asset");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        };
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(service.Current.Failure, Is.EqualTo(fault switch
        {
            "tls" => Failure.TlsRejected,
            "redirect" => Failure.Validation,
            "rate-limit" => Failure.Transient,
            _ => Failure.Unavailable
        }));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
        fixture.Response = null;
        if (fault != "redirect")
        {
            int requests = fixture.Requests.Count;
            await ui.ClickAsync("CheckForUpdates");
            Assert.That(fixture.Requests.Count, Is.EqualTo(requests));
        }
        fixture.Clock.Now += TimeSpan.FromHours(1);
        await ui.ClickAsync("CheckForUpdates");
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Available));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    [AvaloniaTest]
    public async Task Settings_WhenStreamingCancellationIsPending_BlocksReentryAndRejectsLateCompletionThenRetries()
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);
        await ui.ClickAsync("CheckForUpdates");
        TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Response = (name, _) => Task.FromResult(name == ClientUpdateFixture.BundleName ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GatedStream(fixture.Signer.Package, reached, release))
        } : fixture.Respond(name));
        try
        {
            // Act: the first chunk reaches staging; the second read ignores cancellation until released.
            ui.Button("DownloadUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Downloading));
            ui.Button("CancelUpdate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            // Assert: cancellation cannot admit another operation or revive Ready on late completion.
            Assert.That(ui.Button("CheckForUpdates").IsEnabled, Is.False);
            Assert.That((await service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None)).Stage, Is.EqualTo(UpdateStage.Failed));
            release.TrySetResult();
            await ui.DrainAsync();
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Cancelled));
            Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
            Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.False);
            string cache = Path.Combine(fixture.Directory, "packages");
            Assert.That(File.Exists(Path.Combine(cache, ClientUpdateFixture.PackageName)), Is.False);
            Assert.That(System.IO.Directory.GetFiles(cache, "*.partial"), Is.Empty);
            fixture.Response = null;
            await ui.ClickAsync("CheckForUpdates");
            await ui.ClickAsync("DownloadUpdate");
            Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Ready));
            Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await ui.DrainAsync();
        }
    }

    [Test]
    public async Task Composition_WhenConfigurationIsAbsent_StaysInactiveAndCannotRestart()
    {
        // Arrange
        IClientUpdates service = ClientUpdatesComposition.Create();

        // Act
        UpdateStatus check = await service.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        UpdateStatus restart = await service.RestartAsync(CancellationToken.None);

        // Assert
        Assert.That(check.Stage, Is.EqualTo(UpdateStage.Inactive));
        Assert.That(restart.CanRestart, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Settings_WhenClientSkipsReleases_TraversesEveryRetainedKeyBridge(bool missingBridge)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using UpdateFixture middle = new UpdateFixture();
        using UpdateFixture latest = new UpdateFixture();
        byte[] firstActivation = middle.Manifest(sequence: 1, version: "1.0.1", change: document => document["minimumApp"] = "1.0.0");
        byte[] secondActivation = latest.Manifest(sequence: 3, version: "1.0.3");
        byte[] first = fixture.Signer.Transition(middle, firstActivation, sequence: 1, version: "1.0.1");
        byte[] second = middle.Transition(latest, secondActivation, epoch: 2, sequence: 3, version: "1.0.3");
        byte[] manifest = latest.Manifest(sequence: 5);
        fixture.Documents["Postfold.win-x64-stable.manifest.json"] = manifest;
        fixture.Documents["Postfold.win-x64-stable.manifest.sig"] = latest.Sign(manifest);
        if (!missingBridge)
        {
            fixture.Documents["transition-" + fixture.Signer.KeyId + ".json"] = first;
            fixture.Documents["transition-" + fixture.Signer.KeyId + ".sig"] = fixture.Signer.Sign(first);
        }
        fixture.Documents["activation-" + UpdateFixture.Digest(firstActivation) + ".json"] = firstActivation;
        fixture.Documents["activation-" + UpdateFixture.Digest(firstActivation) + ".sig"] = middle.Sign(firstActivation);
        fixture.Documents["transition-" + middle.KeyId + ".json"] = second;
        fixture.Documents["transition-" + middle.KeyId + ".sig"] = middle.Sign(second);
        fixture.Documents["activation-" + UpdateFixture.Digest(secondActivation) + ".json"] = secondActivation;
        fixture.Documents["activation-" + UpdateFixture.Digest(secondActivation) + ".sig"] = latest.Sign(secondActivation);
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);

        // Act
        await ui.ClickAsync("CheckForUpdates");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(missingBridge ? UpdateStage.Failed : UpdateStage.Available));
        Assert.That(ui.Button("DownloadUpdate").IsEnabled, Is.EqualTo(!missingBridge));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
        using UpdateTrustStore trust = UpdateTrustStore.Open(Path.Combine(fixture.Directory, "trust.journal"), fixture.Signer.PublicKey, "stable").Value!;
        Assert.That(trust.State.Epoch, Is.EqualTo(missingBridge ? 0 : 2));
        Assert.That(trust.State.Sequence, Is.EqualTo(missingBridge ? 0 : 5));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Settings_WhenTrustChangesAfterCheck_ReplaysBeforeDownloading(bool corrupt)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        using AuthenticatedClientUpdates service = fixture.Create();
        await using SettingsHarness ui = await SettingsHarness.OpenAsync(service);
        await ui.ClickAsync("CheckForUpdates");
        string journal = Path.Combine(fixture.Directory, "trust.journal");
        if (corrupt)
        {
            File.WriteAllBytes(journal + ".head", [1]);
        }
        else
        {
            using UpdateTrustStore trust = UpdateTrustStore.Open(journal, fixture.Signer.PublicKey, "stable").Value!;
            byte[] later = fixture.Signer.Manifest(sequence: 2, version: "1.2.0");
            Assert.That(trust.Accept(later, fixture.Signer.Sign(later)).IsSuccess, Is.True);
        }

        // Act
        await ui.ClickAsync("DownloadUpdate");

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(fixture.Requests, Does.Not.Contain(ClientUpdateFixture.BundleName));
        Assert.That(ui.Button("RestartToUpdate").IsEnabled, Is.False);
    }

    private sealed class GatedStream(byte[] bytes, TaskCompletionSource reached, TaskCompletionSource release) : MemoryStream(bytes)
    {
        private bool _first = true;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                return await base.ReadAsync(buffer[..((int)Length / 2)], cancellationToken);
            }
            reached.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }

    private sealed class SettingsHarness : IAsyncDisposable
    {
        private readonly MainWindowViewModel _model;
        private readonly MainWindow _main;
        private readonly SettingsWindow _settings;
        private bool _disposed;

        private SettingsHarness(MainWindowViewModel model, MainWindow main, SettingsWindow settings)
        {
            _model = model;
            _main = main;
            _settings = settings;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _settings.Close();
            await DrainAsync();
            _main.Close();
            await _model.DisposeAsync();
        }

        internal static async Task<SettingsHarness> OpenAsync(IClientUpdates service)
        {
            IMailApplication application = Substitute.For<IMailApplication>();
            IDesktopIntegration desktop = Substitute.For<IDesktopIntegration>();
            desktop.ReadPreferencesAsync(Arg.Any<CancellationToken>()).Returns(DesktopPreferences.Default);
            MainWindowViewModel model = new MainWindowViewModel(application);
            MainWindow main = new MainWindow
            {
                DataContext = model
            };
            main.Configure(application, null, desktop, updates: service);
            main.Show();
            main.OpenSettings("Updates");
            SettingsHarness harness = new SettingsHarness(model, main, main.OwnedWindows.OfType<SettingsWindow>().Single());
            await harness.DrainAsync();
            return harness;
        }

        internal Button Button(string name)
        {
            return _settings.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
        }

        internal async Task ClickAsync(string name)
        {
            Assert.That(Button(name).IsEnabled, Is.True, name);
            Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await DrainAsync();
        }

        internal Task DrainAsync()
        {
            MethodInfo drain = typeof(SettingsWindow).Assembly.GetType("Postfold.UI.Controls.FormControls")!
                .GetMethod("DrainAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (Task)drain.Invoke(null, new object[] { _main })!;
        }
    }
}

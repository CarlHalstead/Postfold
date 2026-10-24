using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Storage.Stores;
using Postfold.UI.Resources;
using Postfold.UI.Views;
using System;
using System.Collections;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

/// <summary>Explicit native qualification host; synthetic HTTP/key/profile only, never part of the shipping app.</summary>
internal sealed class InstalledUpdateQualification : Application
{
    private static Configuration _configuration = null!;
    [ThreadStatic]
    private static bool _capturing;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Postfold.UI/"))
        {
            Source = new Uri("avares://Postfold.UI/Resources/MailStyles.axaml")
        });
        MailTheme.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        IClassicDesktopStyleApplicationLifetime desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        Configuration configuration = _configuration;
        string installation = configuration.Installation;
        Compatibility compatibility = ReadCompatibility();
        Assembly subject = typeof(DesktopSession).Assembly;
        if (Hash(subject.Location) != Hash(Path.Combine(installation, "current", "Postfold.dll")))
        {
            throw new InvalidDataException("Qualification must execute the packaged Desktop assembly.");
        }
        using Stream inventory = subject.GetManifestResourceStream("Postfold.ProductionNativeInventory.json") ?? throw new InvalidDataException("Packaged native inventory is missing.");
        (ClientUpdateConfiguration admitted, NativeRestartConfiguration native) = ProductionUpdatesPreparation.ReviewInventory(
            inventory,
            installation,
            subject.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            compatibility.TargetFormat,
            compatibility.TargetMigration
        );
        if (admitted.InstalledVersion != configuration.InstalledVersion)
        {
            throw new InvalidDataException("Installed candidate version differs from the qualified source.");
        }
        Write("packaged-components.json", new { desktopSha256 = Hash(subject.Location), storageSha256 = compatibility.StorageSha256, native.BrokerSha256, native.HelperSha256 });
        using JsonDocument selected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(configuration.Area, "server", "Postfold.win-x64-beta.manifest.json")));
        string targetVersion = selected.RootElement.GetProperty("version").GetString()!;
        ClientUpdateConfiguration updates = new ClientUpdateConfiguration(
            new Uri("https://github.com/CarlHalstead/Postfold/releases/download/v" + targetVersion + "/"),
            File.ReadAllText(Path.Combine(configuration.Area, "synthetic-public-spki.txt")),
            "beta",
            configuration.InstalledVersion,
            compatibility.TargetFormat,
            compatibility.TargetMigration,
            Path.Combine(installation, "updates", "win-x64", "beta")
        );
        SystemUpdateConnection connection = new SystemUpdateConnection();
        Write("native-cost.json", new { cost = connection.Current.ToString() });
        DesktopSession session = new DesktopSession(
            desktop,
            profilesDirectory: Path.Combine(installation, "profiles"),
            updateConfiguration: updates,
            updateHandler: new Transport(Path.Combine(configuration.Area, "server")),
            updateConnection: connection,
            nativeRestart: native
        );
        desktop.MainWindow = session.Window;
        desktop.Exit += (_, _) => Write("old-exit.json", new { normalLifetimeExit = true, time = DateTimeOffset.UtcNow });
        session.Window.Opened += async (_, _) =>
        {
            try
            {
                await session.Startup;
                if (connection.Current != UpdateConnection.Unmetered)
                {
                    throw new InvalidDataException("Native Windows cost must be unmetered for this qualification.");
                }
                foreach (Window child in session.Window.OwnedWindows.ToArray())
                {
                    child.Close();
                }
                session.Window.OpenSettings("Updates");
                SettingsWindow settings = session.Window.OwnedWindows.OfType<SettingsWindow>().Single();
                await session.Window.WaitForActionsAsync();
                T Control<T>(string name) where T : Control
                {
                    return settings.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
                }
                async Task ClickAsync(string name)
                {
                    Button button = Control<Button>(name);
                    if (!button.IsEnabled)
                    {
                        throw new InvalidDataException("Qualification action is disabled: " + name);
                    }
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await session.Window.WaitForActionsAsync();
                }
                ScheduledClientUpdates service = (ScheduledClientUpdates)typeof(DesktopSession).GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                Control<CheckBox>("UpdateChecksEnabled").IsChecked = true;
                Control<CheckBox>("UpdateDownloadsEnabled").IsChecked = false;
                await ClickAsync("SaveUpdatePreferences");
                await service.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (service.Current.Stage != UpdateStage.Available)
                {
                    throw new InvalidDataException("Real discovery did not select the fixture release: " + service.Current);
                }
                Control<CheckBox>("UpdateDownloadsEnabled").IsChecked = true;
                await ClickAsync("SaveUpdatePreferences");
                await ClickAsync("DownloadUpdate");
                if (service.Current.Stage != UpdateStage.Ready || !service.Current.CanRestart)
                {
                    throw new InvalidDataException("Real download did not produce a restartable release: " + service.Current);
                }
                // The reopened shipping app must not contact production discovery or touch real-user
                // update trust. Persist disabled checks after staging; explicit Restart remains enabled.
                Control<CheckBox>("UpdateChecksEnabled").IsChecked = false;
                await ClickAsync("SaveUpdatePreferences");
                session.Window.OpenCompose(activation: new ComposeContent(
                    "synthetic@example.invalid",
                    [],
                    "Installed updater qualification",
                    "Initial unsaved synthetic draft",
                    DateTimeOffset.UnixEpoch,
                    "synthetic@example.invalid"
                ));
                ComposeWindow draft = session.Window.OwnedWindows.OfType<ComposeWindow>().Single();
                draft.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "Body").Text = "Final dirty draft before native restart";
                Write("before-restart.json", new { draft = draft.DraftId, status = service.Current });
                service.Changed += (_, _) =>
                {
                    if (service.Current.Stage == UpdateStage.RestartBlocked)
                    {
                        Write("restart-blocked.json", service.Current);
                        Write("failure.json", new { message = "Native restart was blocked", status = service.Current });
                        Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
                    }
                };
                await ClickAsync("RestartToUpdate");
            }
            catch (Exception exception)
            {
                Write("failure.json", new { type = exception.GetType().Name, exception.Message, exception.StackTrace });
                desktop.Shutdown(1);
            }
        };
        base.OnFrameworkInitializationCompleted();
    }

    internal static void Run(string mode, string specification)
    {
        _configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllBytes(specification))!;
        if (!_configuration.Installation.StartsWith(_configuration.Area + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(_configuration.Area, ".synthetic-update-qualification")))
        {
            throw new InvalidDataException("Use the explicitly marked isolated qualification directory.");
        }
        if (mode == "prepare")
        {
            PrepareAsync().GetAwaiter().GetResult();
        }
        else if (mode == "run")
        {
            AppDomain.CurrentDomain.FirstChanceException += CaptureNativeFailure;
            try
            {
                Environment.ExitCode = AppBuilder.Configure<InstalledUpdateQualification>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).StartWithClassicDesktopLifetime([]);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= CaptureNativeFailure;
            }
        }
        else if (mode == "audit")
        {
            AuditAsync().GetAwaiter().GetResult();
        }
        else
        {
            throw new ArgumentException("Unknown installed-update qualification mode.", nameof(mode));
        }
    }

    private static async Task PrepareAsync()
    {
        Configuration configuration = _configuration;
        Compatibility compatibility = ReadCompatibility();
        Write("update-compatibility.json", compatibility);
        string server = Path.Combine(configuration.Area, "server");
        Directory.CreateDirectory(server);
        string package = Directory.GetFiles(configuration.ReleaseDirectory, "*-full.nupkg").Single();
        string feed = Path.Combine(configuration.ReleaseDirectory, "releases.win-x64-beta.json");
        using JsonDocument index = JsonDocument.Parse(File.ReadAllBytes(feed));
        string target = index.RootElement.GetProperty("Assets")[0].GetProperty("Version").GetString()!;
        using RSA key = RSA.Create(3072);
        string pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(Path.Combine(configuration.Area, "synthetic-public-spki.txt"), pin);
        File.Copy(package, Path.Combine(server, Path.GetFileName(package)));
        File.Copy(feed, Path.Combine(server, Path.GetFileName(feed)));
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "PostfoldUpdateManifest",
            schema = 1,
            keyId = UpdateTrust.KeyId(pin),
            product = "Postfold",
            rid = "win-x64",
            channel = "beta",
            toolChannel = "win-x64-beta",
            sequence = 1,
            version = target,
            sourceCommit = configuration.SourceCommit,
            minimumApp = configuration.InstalledVersion,
            minimumFormat = compatibility.MinimumFormat,
            maximumFormat = compatibility.MaximumFormat,
            targetFormat = compatibility.TargetFormat,
            originMigrations = compatibility.OriginMigrations,
            targetMigration = compatibility.TargetMigration,
            feed = new { name = Path.GetFileName(feed), length = new FileInfo(feed).Length, sha256 = Hash(feed) },
            package = new { name = Path.GetFileName(package), length = new FileInfo(package).Length, sha256 = Hash(package) }
        });
        File.WriteAllBytes(Path.Combine(server, "Postfold.win-x64-beta.manifest.json"), document);
        File.WriteAllBytes(Path.Combine(server, "Postfold.win-x64-beta.manifest.sig"), key.SignData(document, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        string[] leaves = Directory.GetFiles(server);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(server, "Postfold-" + target + "-win-x64.zip"), ZipArchiveMode.Create))
        {
            foreach (string leaf in leaves)
            {
                ZipArchiveEntry entry = archive.CreateEntryFromFile(leaf, Path.GetFileName(leaf));
                entry.ExternalAttributes = 0;
            }
        }
        await using SqliteProfile profile = await EngineFixture.OpenAsync(Path.Combine(configuration.Installation, "profiles", "default"));
        await profile.SaveDesktopPreferencesAsync(DesktopPreferences.Default with
        {
            Updates = new UpdatePreferences(ChecksEnabled: false)
        }, CancellationToken.None);
        MailEndpoint endpoint = new MailEndpoint("synthetic.invalid", 993, TlsMode.Implicit, "synthetic@example.invalid", "missing");
        await profile.ConfigureAccountAsync(EngineFixture.Account, endpoint, endpoint, CancellationToken.None);
        FrozenSubmission uncertain = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "uncertain MIME must survive"));
        await profile.QueueAsync(uncertain, CancellationToken.None);
        OutboxEntry claim = (await profile.ClaimAsync(uncertain.Id, uncertain.Account, CancellationToken.None))!;
        await profile.FinishAsync(claim, new SubmissionOutcome(
            SubmissionState.Uncertain,
            SubmissionPhase.AwaitingAcknowledgement,
            uncertain.Recipients.Select(address => new RecipientEvidence(address, RecipientState.Uncertain, null)).ToImmutableArray(),
            Failure.NeedsReview
        ), CancellationToken.None);
        FrozenSubmission queued = EngineFixture.Submission(await EngineFixture.BlobAsync(profile, "queued MIME must survive"));
        await profile.QueueAsync(queued, CancellationToken.None);
        Write("durable-before.json", new { uncertain, queued });
    }

    private static void CaptureNativeFailure(object? sender, FirstChanceExceptionEventArgs args)
    {
        string? trace = args.Exception.StackTrace;
        if (_capturing || trace is null || !(trace.Contains("StagedNativeUpdate", StringComparison.Ordinal) || trace.Contains("NativeUpdateLauncher", StringComparison.Ordinal)))
        {
            return;
        }
        _capturing = true;
        try
        {
            Write("native-launch-failure.json", new { type = args.Exception.GetType().Name, args.Exception.Message, trace });
        }
        finally
        {
            _capturing = false;
        }
    }

    private static async Task AuditAsync()
    {
        using JsonDocument before = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_configuration.Area, "durable-before.json")));
        using JsonDocument restart = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_configuration.Area, "before-restart.json")));
        await using SqliteProfile profile = await EngineFixture.OpenAsync(Path.Combine(_configuration.Installation, "profiles", "default"));
        ImmutableArray<OutboxEntry> entries = await profile.OutboxAsync(EngineFixture.Account, CancellationToken.None);
        foreach (string name in new[] { "uncertain", "queued" })
        {
            FrozenSubmission original = before.RootElement.GetProperty(name).Deserialize<FrozenSubmission>()!;
            OutboxEntry saved = entries.Single(entry => entry.Submission.Id == original.Id);
            if (JsonSerializer.Serialize(saved.Submission) != JsonSerializer.Serialize(original) || saved.State != (name == "uncertain" ? SubmissionState.Uncertain : SubmissionState.Queued))
            {
                throw new InvalidDataException("Durable outbox changed: " + name);
            }
            using Stream mime = await profile.Blobs.OpenAsync(saved.Submission.Mime, CancellationToken.None);
            using StreamReader reader = new StreamReader(mime);
            if (!string.Equals(await reader.ReadToEndAsync(), name + " MIME must survive", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Frozen MIME bytes changed.");
            }
        }
        Guid id = restart.RootElement.GetProperty("draft").GetGuid();
        DraftSnapshot draft = await profile.ReadAsync(id, EngineFixture.Account, CancellationToken.None) ?? throw new InvalidDataException("Dirty draft was lost.");
        using Stream content = await profile.Blobs.OpenAsync(draft.Content, CancellationToken.None);
        using StreamReader body = new StreamReader(content);
        if (!(await body.ReadToEndAsync()).Contains("Final dirty draft before native restart", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Final dirty draft content was not saved.");
        }
        Write("durable-after.json", new { passed = true, draft = id, outbox = entries.Length });
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Compatibility ReadCompatibility()
    {
        Assembly storage = typeof(SqliteProfile).Assembly;
        if (storage.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion != _configuration.InstalledVersion + "+" + _configuration.SourceCommit ||
            Hash(storage.Location) != Hash(Path.Combine(_configuration.Installation, "current", "Postfold.Storage.dll")))
        {
            throw new InvalidDataException("Qualification must use the candidate's exact packaged storage assembly.");
        }
        Type manifest = storage.GetType("Postfold.Storage.Models.ProfileMigrationManifest")!;
        object[] versions = ((IEnumerable)manifest.GetField("Versions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Cast<object>().ToArray();
        int[] formats = versions.Select(version => (int)version.GetType().GetProperty("Format")!.GetValue(version)!).ToArray();
        string[] migrations = versions.Select(version => (string)version.GetType().GetProperty("Id")!.GetValue(version)!).ToArray();
        return new Compatibility(_configuration.InstalledVersion, _configuration.SourceCommit, formats.Min(), formats.Max(), formats[^1], migrations, migrations[^1], Hash(storage.Location));
    }

    private static void Write(string name, object value)
    {
        File.WriteAllBytes(Path.Combine(_configuration.Area, name), JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private sealed record Configuration(string Area, string Installation, string ReleaseDirectory, string InstalledVersion, string SourceCommit);
    private sealed record Compatibility(string Version, string SourceCommit, int MinimumFormat, int MaximumFormat, int TargetFormat, string[] OriginMigrations, string TargetMigration, string StorageSha256);

    private sealed class Transport(string directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, Path.GetFileName(request.RequestUri!.AbsolutePath));
            return Task.FromResult(File.Exists(path) ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(path))
            } : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

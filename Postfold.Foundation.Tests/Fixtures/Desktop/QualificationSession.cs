using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Services;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Services;
using Postfold.Desktop.Stores;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

internal sealed class QualificationSession
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private DesktopMailRuntime? _host;
    private Task _starting = Task.CompletedTask;
    private bool _closing;
    private bool _finished;
    private bool _started;
    private bool _exitRequested;
    private DesktopIntegration? _integration;
    private WindowsNewMailNotification? _notifications;
    private WindowsHtmlReadingService? _html;
    private QualificationImageTransport? _qualificationImages;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    internal QualificationSession(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        _desktop = desktop;
        _window = window;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Opened += (_, _) =>
        {
            if (!_started)
            {
                _started = true;
                _starting = StartAsync();
            }
        };
        window.Closing += async (_, args) =>
        {
            if (_finished)
            {
                return;
            }

            if (!_exitRequested && !_closing && _integration?.TrayEnabled == true)
            {
                args.Cancel = true;
                _window.Hide();
                return;
            }

            args.Cancel = true;
            if (_closing)
            {
                return;
            }

            _closing = true;
            await _lifetime.CancelAsync();
            await _starting;
            if (!await window.FlushComposeAsync())
            {
                _closing = false;
                _exitRequested = false;
                return;
            }

            if (window.DataContext is MainWindowViewModel model)
            {
                if (_html is not null)
                {
                    await _html.DisposeAsync();
                }

                await model.DisposeAsync();
            }

            if (_host is not null)
            {
                await _host.DisposeAsync();
            }

            _integration?.Dispose();
            _notifications?.Dispose();
            _qualificationImages?.Dispose();

            _finished = true;
            _desktop.Shutdown();
        };
    }

    private async Task StartAsync()
    {
        string[] arguments = _desktop.Args ?? [];
        long started = QualificationProgram.Started;
        string? measurement = arguments is ["--measure-client" or "--measure-client-auto", string output] ? output : null;
        Dictionary<string, double> startupStages = new Dictionary<string, double>();
        if (measurement is not null)
        {
            _window.Resources["StartupStages"] = startupStages;
        }
        Stage("window-opened");
        string? qualification = arguments is ["--qualify-client", string evidenceOutput] ? evidenceOutput : null;
        string? design = arguments is ["--qualify-design", string designOutput] ? designOutput : null;
        bool scale = arguments.Contains("--demo-scale") || measurement is not null || design is not null;
        qualification ??= design;
        bool demo = arguments.Contains("--demo") || scale || qualification is not null;
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Postfold",
            design is not null ? "synthetic-design-v2" : qualification is not null ? "synthetic-qualification-v1" : scale ? "synthetic-scale-v1" :
                demo ? "synthetic-demo-v1" : "profiles/default"
        );
        try
        {
            EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);
            Outcome<DesktopMailRuntime> opened = await DesktopMailRuntime.OpenAsync(
                directory,
                new EnginePolicy(!demo, false, false),
                TimeProvider.System,
                limits,
                CancellationToken.None
            );
            if (!opened.IsSuccess)
            {
                _window.Title = "Postfold — " + opened.Failure;
                return;
            }

            _host = opened.Value!;
            Stage("profile-opened");
            WindowsCredentialStore secure = new WindowsCredentialStore(Path.Combine(directory, "credentials"));
            SessionCredentialStore session = new SessionCredentialStore();
            if (demo)
            {
                bool populated = (await _host.Catalog.AccountsAsync(_lifetime.Token)).IsEmpty;
                await _host.SeedSyntheticAsync(scale ? 100000 : 360, _lifetime.Token);
                if (design is not null)
                {
                    await _host.SeedDesignFixtureAsync(_lifetime.Token);
                }
                else if (qualification is not null)
                {
                    await _host.SeedReadingFixtureAsync(_lifetime.Token);
                }
                if (measurement is not null && populated)
                {
                    _window.Tag = "InitialPopulation";
                }
            }

            await _host.RestoreAccountsAsync(new CombinedCredentialStore(secure, session), CancellationToken.None);
            Stage("synthetic-seed-and-account-restore");
            IMailApplication application = qualification is null ? _host.Application :
                new QualificationMailApplication(_host.Application);
            MainWindowViewModel model = new MainWindowViewModel(application)
            {
                Title = demo ? Strings.Synthetic : "Postfold"
            };
            _window.DataContext = model;
            _notifications = new WindowsNewMailNotification(_window, TimeProvider.System);
            _integration = new DesktopIntegration(_window, _host.DesktopPreferences, _notifications);
            _integration.ExitRequested += (_, _) =>
            {
                _exitRequested = true;
                _window.Close();
            };
            DesktopPreferences preferences = await _integration.ReadPreferencesAsync(_lifetime.Token);
            Failure configured = await _integration.ConfigureAsync(preferences, _lifetime.Token);
            if (configured != Failure.None)
            {
                await _integration.ConfigureAsync(preferences with
                {
                    TrayEnabled = false
                }, _lifetime.Token);
            }

            QualificationAccountSetup? fixtureSetup = qualification is null ? null :
                new QualificationAccountSetup(_host.Catalog, new AccountSetup(_host, secure, session, _host.Identity));
            _window.Configure(application, fixtureSetup is not null ? fixtureSetup :
                demo ? null : new AccountSetup(_host, secure, session, _host.Identity), _integration, _host);
            // Windows native isolation, reader lifetime and accessibility gates are qualified for this stage.
            // Only the authored qualification profile substitutes in-process image responses.
            _qualificationImages = qualification is null ? null : new QualificationImageTransport();
            IRemoteImageFetcher images = _qualificationImages is null ? new RemoteImageFetcher() :
                new RemoteImageFetcher(_qualificationImages);
            _html = new WindowsHtmlReadingService(
                _window,
                application,
                images,
                Path.Combine(directory, "html-renderer"),
                qualified: true
            );
            _window.ConfigureHtml(_html);
            _window.ConfigureIdentity(_host.Identity);
            Stage("services-composed");
            await _window.LoadAppearanceAsync(_host.Appearance);
            Stage("appearance-loaded");
            await model.InitialiseAsync();
            Stage("first-page-loaded");
            if (measurement is not null)
            {
                await File.WriteAllTextAsync(measurement + ".startup.json", JsonSerializer.Serialize(startupStages) + "\n", _lifetime.Token);
            }
            if (design is not null)
            {
                try
                {
                    await UiDesignQualification.RunAsync(_window, model, fixtureSetup!, design, _lifetime.Token);
                }
                catch (Exception exception)
                {
                    await File.WriteAllTextAsync(design, "Native design qualification failed:\n" + exception, _lifetime.Token);
                }
                _exitRequested = true;
                _window.Close();
                return;
            }
            if (qualification is not null)
            {
                ClientQualification.Open(
                    _window,
                    _host,
                    (QualificationMailApplication)application,
                    fixtureSetup!,
                    _integration,
                    qualification
                );
            }
            long observedMessages = model.CachedMessageCount;
            model.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(MainWindowViewModel.CachedMessageCount))
                {
                    long current = model.CachedMessageCount;
                    if (current > observedMessages)
                    {
                        Failure notified = _integration.NotifyNewMail();
                        if (notified != Failure.None)
                        {
                            model.ReportNotificationFailure();
                        }
                    }

                    observedMessages = current;
                }
            };
            if (measurement is not null)
            {
                await ClientPerformanceProbe.RunAsync(
                    _window,
                    model,
                    measurement,
                    started,
                    _window.Tag as string == "InitialPopulation",
                    _lifetime.Token
                );
                if (arguments.Contains("--measure-client-auto"))
                {
                    _exitRequested = true;
                    _window.Close();
                    return;
                }
            }
            string? activation = arguments.FirstOrDefault(item => item.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
            if (activation is not null)
            {
                Outcome<ComposeContent> content = MailtoActivation.Parse(activation);
                if (content.IsSuccess)
                {
                    _window.OpenCompose(activation: content.Value, applyIdentitySignature: true);
                }
                else
                {
                    _window.Title = "Postfold — " + content.Failure;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _window.Title = "Postfold — local profile unavailable";
        }

        void Stage(string name)
        {
            if (measurement is not null)
            {
                startupStages[name] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
    }
}

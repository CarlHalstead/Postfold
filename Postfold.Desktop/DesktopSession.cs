using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Postfold.Core;
using Postfold.UI;

namespace Postfold.Desktop;

internal sealed class DesktopSession
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private EngineHost? _host;
    private Task _starting = Task.CompletedTask;
    private bool _closing;
    private bool _finished;
    private bool _started;
    private bool _exitRequested;
    private DesktopIntegration? _integration;
    private WindowsNewMailNotification? _notifications;
    private WindowsHtmlReadingService? _html;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    internal DesktopSession(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
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

            _finished = true;
            _desktop.Shutdown();
        };
    }

    private async Task StartAsync()
    {
        string[] arguments = _desktop.Args ?? [];
        long started = Program.Started;
        string? measurement = arguments is ["--measure-client", string output] ? output : null;
        bool scale = arguments.Contains("--demo-scale") || measurement is not null;
        bool demo = arguments.Contains("--demo") || scale;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Postfold", scale ? "synthetic-scale-v1" :
                demo ? "synthetic-demo-v1" : "profiles/default");
        try
        {
            EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);
            Outcome<EngineHost> opened = await EngineHost.OpenAsync(directory, new EnginePolicy(!demo, false, false),
                TimeProvider.System, limits, CancellationToken.None);
            if (!opened.IsSuccess)
            {
                _window.Title = "Postfold — " + opened.Failure;
                return;
            }

            _host = opened.Value!;
            WindowsCredentialStore secure = new WindowsCredentialStore(Path.Combine(directory, "credentials"));
            SessionCredentialStore session = new SessionCredentialStore();
            if (demo)
            {
                bool populated = (await _host.Catalog.AccountsAsync(_lifetime.Token)).IsEmpty;
                await _host.SeedSyntheticAsync(scale ? 100000 : 360, _lifetime.Token);
                if (measurement is not null && populated)
                {
                    _window.Tag = "InitialPopulation";
                }
            }

            await _host.RestoreAccountsAsync(new CombinedCredentialStore(secure, session), CancellationToken.None);
            MainWindowViewModel model = new MainWindowViewModel(_host.Application)
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

            _window.Configure(_host.Application, demo ? null : new AccountSetup(_host, secure, session), _integration);
            // Native H01-H04 evidence is still failing; no environment or user-preference bypass is provided.
            _html = new WindowsHtmlReadingService(_window, _host.Application, new RemoteImageFetcher(),
                Path.Combine(directory, "html-renderer"), qualified: false);
            _window.ConfigureHtml(_html);
            _window.ConfigureIdentity(_host.Identity);
            await _window.LoadAppearanceAsync(_host.Appearance);
            await model.InitialiseAsync();
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
                await ClientPerformanceProbe.RunAsync(_window, model, measurement, started,
                    _window.Tag as string == "InitialPopulation", _lifetime.Token);
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
    }
}

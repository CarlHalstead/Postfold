using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Services;
using Postfold.Desktop.Services;
using Postfold.Desktop.Stores;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Composition;

internal sealed class DesktopSession
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly ProfileSelectionStore _profiles;
    private DesktopMailRuntime? _host;
    private Task _starting = Task.CompletedTask;
    private Task _synchronising = Task.CompletedTask;
    private bool _closing;
    private bool _finished;
    private bool _started;
    private bool _exitRequested;
    private DesktopIntegration? _integration;
    private WindowsNewMailNotification? _notifications;
    private WindowsHtmlReadingService? _html;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    internal DesktopSession(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        string? profilesDirectory = null
    )
    {
        _desktop = desktop;
        _window = window;
        _profiles = new ProfileSelectionStore(profilesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Postfold",
            "profiles"
        ));
        window.FreshProfileRequested += (_, _) =>
        {
            if (_starting.IsCompleted && !_closing && _host is null)
            {
                _starting = StartFreshAsync();
            }
        };
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
            await _synchronising;
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

    internal Task Startup => _starting;

    private async Task<bool> StartAsync(string? directory = null)
    {
        string[] arguments = _desktop.Args ?? [];
        try
        {
            directory ??= Program.ProfileDirectory ?? _profiles.ReadDirectory();
            EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);
            Outcome<DesktopMailRuntime> opened = await DesktopMailRuntime.OpenAsync(
                directory,
                new EnginePolicy(true, false, false),
                TimeProvider.System,
                limits,
                CancellationToken.None
            );
            if (!opened.IsSuccess)
            {
                _window.ReportStartupFailure(opened.Failure, profilesDirectory: Path.GetDirectoryName(directory));
                return false;
            }
            _host = opened.Value!;
            WindowsCredentialStore secure = new WindowsCredentialStore(Path.Combine(directory, "credentials"));
            SessionCredentialStore session = new SessionCredentialStore();
            await _host.RestoreAccountsAsync(new CombinedCredentialStore(secure, session), _lifetime.Token);
            IMailApplication application = _host.Application;
            MainWindowViewModel model = new MainWindowViewModel(application);
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
            _window.Configure(application, new AccountSetup(_host, secure, session, _host.Identity), _integration, _host);
            _html = new WindowsHtmlReadingService(
                _window,
                application,
                new RemoteImageFetcher(),
                Path.Combine(directory, "html-renderer"),
                qualified: true
            );
            _window.ConfigureHtml(_html);
            _window.ConfigureIdentity(_host.Identity);
            await _window.LoadAppearanceAsync(_host.Appearance);
            await model.InitialiseAsync();
            _window.OpenAccountSetupIfNeeded();
            _synchronising = SynchroniseAccountsAsync(model);
            long observedMessages = model.CachedMessageCount;
            model.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(MainWindowViewModel.CachedMessageCount))
                {
                    long current = model.CachedMessageCount;
                    if (current > observedMessages && _integration.NotifyNewMail() != Failure.None)
                    {
                        model.ReportNotificationFailure();
                    }
                    observedMessages = current;
                }
            };
            string? activation = arguments.FirstOrDefault(item => item.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
            if (activation is not null)
            {
                Outcome<ComposeContent> content = MailtoActivation.Parse(activation);
                if (content.IsSuccess)
                {
                    _window.OpenCompose(activation: content.Value);
                }
                else
                {
                    model.ReportReadingResult(content.Failure);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _window.ReportStartupFailure(
                Failure.Unavailable,
                _host is null,
                directory is null ? _profiles.DirectoryPath : Path.GetDirectoryName(directory)
            );
        }
        return false;
    }

    private async Task StartFreshAsync()
    {
        _window.BeginProfileStartup();
        string directory = _profiles.NewDirectory();
        if (!await StartAsync(directory))
        {
            return;
        }
        try
        {
            _profiles.SaveDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_window.DataContext is MainWindowViewModel model)
            {
                model.ReportSendStatus(Strings.Get("ProfileSelectionNotSaved"));
            }
        }
    }

    private async Task SynchroniseAccountsAsync(MainWindowViewModel model)
    {
        try
        {
            if (_host is null)
            {
                return;
            }
            foreach (Postfold.Core.Models.Accounts.AccountConfiguration account in model.Accounts.ToArray())
            {
                Failure result = await _host.Application.SynchroniseAsync(account.Id, _lifetime.Token);
                await model.InitialiseAsync();
                if (result != Failure.None)
                {
                    model.ReportReadingResult(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            model.ReportReadingResult(Failure.Unavailable);
        }
    }
}

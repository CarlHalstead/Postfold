using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Models.Updates;
using Postfold.Core.Services;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Authentication;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services;
using Postfold.Desktop.Services.Authentication;
using Postfold.Desktop.Services.Updates;
using Postfold.Desktop.Stores;
using Postfold.UI.Resources;
using Postfold.UI.ViewModels;
using Postfold.UI.Views;
using Postfold.UpdateBroker.Models;
using Postfold.UpdateBroker.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Postfold.Desktop.Composition;

internal sealed class DesktopSession : IUpdateShutdown, IUpdateRestart
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly ProfileSelectionStore _profiles;
    private readonly Task<Outcome<DesktopMailRuntime>> _opening;
    private string? _profileDirectory;
    private DesktopMailRuntime? _host;
    private WindowsCredentialStore? _secure;
    private DesktopPreferences? _startupPreferences;
    private MainWindowViewModel? _startupModel;
    private Task _starting = Task.CompletedTask;
    private Task _synchronising = Task.CompletedTask;
    private Task _resumingUpdates = Task.CompletedTask;
    private bool _closing;
    private bool _finished;
    private bool _started;
    private bool _exitRequested;
    private DesktopIntegration? _integration;
    private WindowsNewMailNotification? _notifications;
    private WindowsHtmlReadingService? _html;
    private IClientUpdates? _updates;
    private ClientUpdateConfiguration? _updateConfiguration;
    private readonly TimeProvider? _updateTime;
    private readonly HttpMessageHandler? _updateHandler;
    private readonly IUpdateConnection? _updateConnection;
    private NativeRestartConfiguration? _nativeRestart;
    private bool _restartPending;
    private string? _shutdownExplanation;
    private bool _powerHook;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    internal DesktopSession(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow? window = null,
        string? profilesDirectory = null,
        ClientUpdateConfiguration? updateConfiguration = null,
        TimeProvider? updateTime = null,
        HttpMessageHandler? updateHandler = null,
        IUpdateConnection? updateConnection = null,
        NativeRestartConfiguration? nativeRestart = null
    )
    {
        if (nativeRestart is not null && updateConfiguration is null)
        {
            throw new ArgumentException("Native restart requires its matching client configuration.", nameof(nativeRestart));
        }
        _desktop = desktop;
        _updateConfiguration = updateConfiguration;
        _updateTime = updateTime;
        _updateHandler = updateHandler;
        _updateConnection = updateConnection;
        _nativeRestart = nativeRestart;
        _profiles = new ProfileSelectionStore(profilesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Postfold",
            "profiles"
        ));
        // Admission runs off the dispatcher while the native window is constructed and shown.
        // Account restoration follows complete admission; StartAsync observes both before publishing mail services.
        _opening = OpenProfileAsync();
        _window = window ?? new MainWindow();
        window = _window;
        // This session owns child saves and task draining. Avalonia's children-first default can cancel
        // the owner close before our asynchronous barrier is reached.
        window.ClosingBehavior = WindowClosingBehavior.OwnerWindowOnly;
        window.Activated += (_, _) => ResumeUpdates();
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
            if (_restartPending)
            {
                return;
            }
            if (_closing)
            {
                return;
            }

            try
            {
                if (await PrepareUpdateShutdownCoreAsync(CancellationToken.None, null, ordinaryClose: true) == Failure.None)
                {
                    _desktop.Shutdown();
                }
            }
            catch (OperationCanceledException)
            {
                _exitRequested = false;
            }
        };
    }

    internal Task Startup => _starting;

    internal MainWindow Window => _window;

    public async Task<UpdateStatus> RestartAsync(VerifiedUpdateSource release, CancellationToken cancellationToken)
    {
        bool admitted = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closing || _finished || _restartPending || _nativeRestart is null || _updateConfiguration is null)
            {
                return false;
            }
            _restartPending = true;
            _shutdownExplanation = null;
            return true;
        });
        if (!admitted)
        {
            return new UpdateStatus(UpdateStage.RestartBlocked, Failure: Failure.PolicyBlocked);
        }
        Failure failure = Failure.NeedsReview;
        bool committed = false;
        bool authorized = false;
        using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationTokenRegistration cancellation = cancellationToken.Register(() => deadline.Cancel());
        try
        {
            ClientUpdateConfiguration configuration = _updateConfiguration!;
            NativeRestartConfiguration native = _nativeRestart!;
            if (_host is null || configuration.ProfileFormat != _host.ProfileFormat || configuration.Migration != _host.ProfileMigration)
            {
                return new UpdateStatus(UpdateStage.RestartBlocked, Failure: Failure.NeedsReview);
            }
            using Process current = Process.GetCurrentProcess();
            NativeUpdateRequest request = new NativeUpdateRequest(
                current.Id,
                current.StartTime.ToUniversalTime().Ticks,
                native.BrokerSha256,
                native.HelperPath,
                native.HelperSha256,
                native.InstallRoot,
                configuration.InstalledVersion,
                native.InstalledManifestSha256,
                configuration.Channel,
                configuration.ProfileFormat,
                configuration.Migration,
                configuration.PinnedRoot,
                Path.Combine(configuration.StateDirectory, "trust.journal"),
                release.ManifestSha256,
                release.Document.ToArray(),
                release.Signature.ToArray(),
                release.Feed.ToArray(),
                Path.Combine(configuration.StateDirectory, "packages", release.Manifest.Package.Name)
            )
            {
                RestartProfile = _profileDirectory
            };
            await NativeUpdateLauncher.RunRestartAsync(native.BrokerPath, request, async (milestone, token) =>
            {
                if (milestone == "verified")
                {
                    failure = await Dispatcher.UIThread.InvokeAsync(() => PrepareUpdateShutdownCoreAsync(token, () =>
                    {
                        cancellation.Dispose();
                        token.ThrowIfCancellationRequested();
                        if (_updates is ScheduledClientUpdates owner && !owner.TryCommitRestart())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new IOException("Restart admission stopped before shutdown commitment.");
                        }
                        committed = true;
                    }));
                    if (failure != Failure.None)
                    {
                        throw new IOException("Safe shutdown blocked native apply.");
                    }
                }
                else if (milestone == "authorized")
                {
                    authorized = true;
                    await Dispatcher.UIThread.InvokeAsync(() => _desktop.Shutdown());
                }
            }, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!committed && cancellationToken.IsCancellationRequested)
            {
                return new UpdateStatus(UpdateStage.Cancelled);
            }
            failure = Failure.Unavailable;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            if (failure == Failure.None)
            {
                failure = Failure.Unavailable;
            }
        }
        finally
        {
            cancellation.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() => _restartPending = false);
        }
        // If native ownership could not transfer, retain stopped state/evidence and permit ordinary close.
        return new UpdateStatus(
            authorized ? UpdateStage.RestartCommitted : UpdateStage.RestartBlocked,
            release.Manifest.Version,
            Failure: authorized ? Failure.NeedsReview : failure == Failure.None ? Failure.Unavailable : failure
        )
        {
            CanRestart = !_finished && !authorized,
            Explanation = _shutdownExplanation
        };
    }

    /// <summary>No native apply may be authorized unless this owned session barrier succeeds.</summary>
    public Task<Failure> PrepareUpdateShutdownAsync(CancellationToken cancellationToken)
    {
        return PrepareUpdateShutdownCoreAsync(cancellationToken, null);
    }

    /// <summary>Windows automatic resume wakes policy evaluation without input injection or immediate network work.</summary>
    internal Task ObservePowerMessage(uint message, IntPtr reason)
    {
        if (message == 0x0218 && reason == new IntPtr(0x12))
        {
            ResumeUpdates();
            return _resumingUpdates;
        }
        return Task.CompletedTask;
    }

    private async Task<Failure> PrepareUpdateShutdownCoreAsync(CancellationToken cancellationToken, Action? commit, bool ordinaryClose = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closing || _finished)
        {
            return Blocked(Failure.PolicyBlocked, "UpdateShutdownBusy");
        }
        _closing = true;
        bool confirmingDrafts = false;
        (Window Window, bool Enabled)[] children = _window.OwnedWindows.Select(child => (child, child.IsEnabled)).ToArray();
        _window.IsEnabled = false;
        foreach ((Window child, _) in children)
        {
            child.IsEnabled = false;
        }
        try
        {
            await _starting;
            if (_host is null)
            {
                if (ordinaryClose)
                {
                    await _lifetime.CancelAsync();
                    await StopUpdatesAsync();
                    if (!await _window.FlushComposeAsync())
                    {
                        _closing = false;
                        _window.IsEnabled = true;
                        RestoreChildren();
                        return Blocked(Failure.Unavailable, "UpdateShutdownDraftBlocked");
                    }
                    if (_window.DataContext is MainWindowViewModel unopenedModel)
                    {
                        await unopenedModel.DisposeAsync();
                    }
                    _finished = true;
                    return Failure.None;
                }
                _closing = false;
                _window.IsEnabled = true;
                RestoreChildren();
                return Blocked(Failure.PolicyBlocked, "UpdateShutdownProfileBlocked");
            }
            if (_updates is ScheduledClientUpdates scheduled)
            {
                await scheduled.PauseForShutdownAsync(observeRestart: !_restartPending);
            }
            // Active SMTP work is observed, never cancelled to satisfy a restart deadline.
            using CancellationTokenSource drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drain.CancelAfter(TimeSpan.FromSeconds(30));
            await _host.PauseWorkAsync(drain.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ordinaryClose && await _host.HasActiveSubmissionsAsync(cancellationToken))
            {
                _host.ResumeWork();
                _closing = false;
                _window.IsEnabled = true;
                RestoreChildren();
                return Blocked(Failure.NeedsReview, "UpdateShutdownSubmissionBlocked");
            }
            commit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            confirmingDrafts = true;
            if (!await _window.FlushComposeAsync())
            {
                _host.ResumeWork();
                _closing = false;
                _window.IsEnabled = true;
                RestoreChildren();
                return Blocked(Failure.Unavailable, "UpdateShutdownDraftBlocked");
            }
        }
        catch (OperationCanceledException)
        {
            _host?.ResumeWork();
            _closing = false;
            _window.IsEnabled = true;
            RestoreChildren();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _host?.ResumeWork();
            _closing = false;
            _window.IsEnabled = true;
            RestoreChildren();
            return Blocked(Failure.Unavailable, confirmingDrafts ? "UpdateShutdownDraftBlocked" : "UpdateShutdownWorkBlocked");
        }

        void RestoreChildren()
        {
            if (_updates is ScheduledClientUpdates scheduled)
            {
                scheduled.ResumeAfterShutdownRefusal();
            }
            foreach ((Window child, bool enabled) in children)
            {
                child.IsEnabled = enabled;
            }
        }

        // Draft confirmation commits shutdown. Request cancellation no longer abandons owned closure;
        // only the independent handoff deadline may now refuse apply. Never restore stopped services.
        try
        {
            await _lifetime.CancelAsync();
            await _synchronising;
            await StopUpdatesAsync();
            if (_html is not null)
            {
                await _html.DisposeAsync();
            }
            if (_window.DataContext is MainWindowViewModel model)
            {
                await model.DisposeAsync();
            }
            if (_startupModel is not null && !ReferenceEquals(_startupModel, _window.DataContext))
            {
                await _startupModel.DisposeAsync();
            }
            await _host!.DisposeAsync();
            _integration?.Dispose();
            _notifications?.Dispose();
            _finished = true;
            return Failure.None;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Mail remains stopped if closure is incomplete; never re-admit services or launch an updater.
            // Drafts are confirmed durable. Permit ordinary process exit through the native title bar.
            _finished = true;
            return Blocked(Failure.Unavailable, "UpdateShutdownStopped");
        }

        Failure Blocked(Failure failure, string key)
        {
            _shutdownExplanation = Strings.Get(key);
            if (_window.DataContext is MainWindowViewModel model)
            {
                model.ReportSendStatus(_shutdownExplanation);
            }
            return failure;
        }
    }

    private async Task<bool> StartAsync(string? directory = null)
    {
        string[] arguments = _desktop.Args ?? [];
        try
        {
            Outcome<DesktopMailRuntime> opened = directory is null ? await _opening : await OpenProfileAsync(directory);
            directory = _profileDirectory!;
            if (!opened.IsSuccess)
            {
                _window.DataContext ??= new MainWindowViewModel();
                _window.ReportStartupFailure(opened.Failure, profilesDirectory: Path.GetDirectoryName(directory));
                return false;
            }
            _host = opened.Value!;
            WindowsCredentialStore secure = _secure!;
            IMailApplication application = _host.Application;
            MainWindowViewModel model = _startupModel!;
            _notifications = new WindowsNewMailNotification(_window, TimeProvider.System);
            await StartUpdatesAsync();
            _integration = new DesktopIntegration(_window, _host.DesktopPreferences, _notifications, _updates as IClientUpdateActivity);
            _integration.ExitRequested += (_, _) =>
            {
                _exitRequested = true;
                _window.Close();
            };
            DesktopPreferences preferences = _startupPreferences!;
            Failure configured = _integration.RestorePreferences(preferences);
            if (configured != Failure.None)
            {
                await _integration.ConfigureAsync(preferences with
                {
                    TrayEnabled = false
                }, _lifetime.Token);
            }
            _window.Configure(application, new AccountSetup(_host, secure, _host.Identity), _integration, _host, _updates);
            await _window.LoadAppearanceAsync(_host.Appearance);
            _window.DataContext = model;
            // The window owns the model after attachment; only unbound models remain opening-task resources.
            _startupModel = null;
            _html = new WindowsHtmlReadingService(
                _window,
                application,
                new RemoteImageFetcher(),
                Path.Combine(directory, "html-renderer"),
                qualified: true
            );
            _window.ConfigureHtml(_html);
            _window.ConfigureIdentity(_host.Identity);
            _window.OpenAccountSetupIfNeeded();
            _synchronising = SynchroniseAccountsAsync(model, application);
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
            await StopUpdatesAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await StopUpdatesAsync();
            _window.DataContext ??= new MainWindowViewModel();
            _window.ReportStartupFailure(
                Failure.Unavailable,
                _host is null,
                directory is null ? _profiles.DirectoryPath : Path.GetDirectoryName(directory)
            );
        }
        return false;
    }

    private async Task StartUpdatesAsync()
    {
        try
        {
            if (_updateConfiguration is null)
            {
                // Native inventory/hash admission is owned startup IO, never dispatcher or Settings work.
                (ClientUpdateConfiguration Client, NativeRestartConfiguration Native)? reviewed = await Task.Run(
                    () => ProductionUpdatesPreparation.Review(_host!),
                    _lifetime.Token
                );
                if (reviewed is not null)
                {
                    (_updateConfiguration, _nativeRestart) = reviewed.Value;
                }
            }
            _updates = ClientUpdatesComposition.Create(_updateConfiguration, _updateTime, _updateHandler, _host!.DesktopPreferences, _updateConnection, _nativeRestart is null ? null : this);
            if (_updates is ScheduledClientUpdates scheduled)
            {
                await scheduled.StartAsync(_lifetime.Token);
                if (_window.TryGetPlatformHandle()?.HandleDescriptor == "HWND")
                {
                    Win32Properties.AddWndProcHookCallback(_window, OnPowerMessage);
                    _powerHook = true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or XmlException or ArgumentException or FormatException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            await StopUpdatesAsync();
            _updates = new InactiveClientUpdates(Failure.NeedsReview);
        }
    }

    private async Task StopUpdatesAsync()
    {
        if (_powerHook)
        {
            Win32Properties.RemoveWndProcHookCallback(_window, OnPowerMessage);
            _powerHook = false;
        }
        if (_updates is IAsyncDisposable owner)
        {
            await owner.DisposeAsync();
            await _resumingUpdates;
        }
    }

    private void ResumeUpdates()
    {
        if (_updates is ScheduledClientUpdates scheduled && !_closing)
        {
            _resumingUpdates = scheduled.ResumeAsync();
        }
    }

    private IntPtr OnPowerMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        ObservePowerMessage(message, wParam);
        return IntPtr.Zero;
    }

    private Task<Outcome<DesktopMailRuntime>> OpenProfileAsync(
        string? directory = null
    )
    {
        // Keep path-selection IO and any admission failure on the observed startup task.
        return Task.Run(async () =>
        {
            string selected = directory ?? Program.ProfileDirectory ?? _profiles.ReadDirectory();
            _profileDirectory = selected;
            EngineLimits limits = new EngineLimits(2, 256, 3, 200, 33554432, 200, 30, 20, 1073741824);
            Outcome<DesktopMailRuntime> opened = await DesktopMailRuntime.OpenAsync(
                selected,
                new EnginePolicy(true, false, false),
                TimeProvider.System,
                limits,
                CancellationToken.None
            ).ConfigureAwait(false);
            if (opened.IsSuccess)
            {
                // Opening owns these services until StartAsync observes completion. Shutdown awaits
                // startup before draining them, including a partially restored runtime after IO failure.
                _host = opened.Value!;
                _secure = new WindowsCredentialStore(Path.Combine(selected, "credentials"));
                _host.ConfigureProviderClients(ProviderClientConfiguration.Load(
                    Environment.GetEnvironmentVariable("POSTFOLD_PUBLIC_CLIENT_CONFIGURATION")
                ));
                await _host.RestoreAccountsAsync(
                    _secure,
                    new WindowsTokenStore(Path.Combine(selected, "tokens")),
                    new SystemAuthenticationBrowser(),
                    _lifetime.Token
                ).ConfigureAwait(false);
                _startupPreferences = await _host.DesktopPreferences.ReadDesktopPreferencesAsync(_lifetime.Token).ConfigureAwait(false);
                MainWindowViewModel model = new MainWindowViewModel(_host.Application);
                try
                {
                    // This model has no UI subscribers until the opening task is observed on the dispatcher.
                    await model.InitialiseAsync().ConfigureAwait(false);
                    _startupModel = model;
                }
                catch
                {
                    await model.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            return opened;
        });
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

    private async Task SynchroniseAccountsAsync(MainWindowViewModel model, IMailApplication application)
    {
        if (_host is null)
        {
            return;
        }
        MailSynchronisationLoop synchronisation = new MailSynchronisationLoop(
            application,
            TimeProvider.System,
            () => _integration?.MailPollIntervalSeconds ?? 30
        );
        await synchronisation.RunAsync(async (_, _, cancellationToken) =>
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await model.InitialiseAsync(preserveLoadedRows: true);
            });
        }, _lifetime.Token);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Updates;
using Postfold.UI.Resources;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services;

public sealed class DesktopIntegration : IDesktopIntegration, IDisposable
{
    private readonly Window _window;
    private readonly IDesktopPreferenceStore _store;
    private readonly INewMailNotification _notifications;
    private readonly IClientUpdateActivity? _updates;
    private readonly SemaphoreSlim _changes = new SemaphoreSlim(1, 1);
    private DesktopPreferences _preferences = DesktopPreferences.Default;
    private TrayIcon? _tray;
    private bool _disposed;

    public DesktopIntegration(Window window, IDesktopPreferenceStore store, INewMailNotification notifications, IClientUpdateActivity? updates = null)
    {
        _window = window;
        _store = store;
        _notifications = notifications;
        _updates = updates;
    }

    public bool TrayEnabled => !_disposed && _tray is not null && _preferences.TrayEnabled;

    public event EventHandler? ExitRequested;

    public Task<DesktopPreferences> ReadPreferencesAsync(CancellationToken cancellationToken)
    {
        return _store.ReadDesktopPreferencesAsync(cancellationToken);
    }

    public async Task<Failure> ConfigureAsync(DesktopPreferences preferences, CancellationToken cancellationToken)
    {
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                return Failure.Unavailable;
            }

            if (preferences.TrayEnabled && _tray is null && !CreateTray())
            {
                return Failure.Unavailable;
            }

            DesktopPreferences current = await _store.ReadDesktopPreferencesAsync(cancellationToken);
            preferences = preferences with
            {
                Updates = current.Updates
            };
            await _store.SaveDesktopPreferencesAsync(preferences, cancellationToken);
            _preferences = preferences;
            if (_tray is not null)
            {
                _tray.IsVisible = preferences.TrayEnabled;
            }

            if (!preferences.TrayEnabled && !_window.IsVisible)
            {
                Restore();
            }

            return Failure.None;
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<Failure> ConfigureUpdatesAsync(UpdatePreferences preferences, CancellationToken cancellationToken)
    {
        if (preferences.CheckIntervalHours is not (24 or 168))
        {
            return Failure.Validation;
        }
        await _changes.WaitAsync(cancellationToken);
        try
        {
            if (_disposed)
            {
                return Failure.Unavailable;
            }
            DesktopPreferences current = await _store.ReadDesktopPreferencesAsync(cancellationToken);
            DesktopPreferences changed = current with
            {
                Updates = preferences
            };
            await _store.SaveDesktopPreferencesAsync(changed, cancellationToken);
            _preferences = changed;
            _updates?.Configure(preferences);
            return Failure.None;
        }
        finally
        {
            _changes.Release();
        }
    }

    public Failure NotifyNewMail()
    {
        return !_disposed && _preferences.NotificationsEnabled ? _notifications.ShowGeneric() : Failure.None;
    }

    public void Restore()
    {
        if (_disposed)
        {
            return;
        }

        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        if (_tray is not null && Application.Current is Application app)
        {
            TrayIcon.GetIcons(app)?.Remove(_tray);
            _tray.Dispose();
            _tray = null;
        }
    }

    /// <summary>Applies admitted startup preferences without rewriting the user's saved choices.</summary>
    internal Failure RestorePreferences(DesktopPreferences preferences)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || preferences.TrayEnabled && _tray is null && !CreateTray())
        {
            return Failure.Unavailable;
        }

        _preferences = preferences;
        if (_tray is not null)
        {
            _tray.IsVisible = preferences.TrayEnabled;
        }
        return Failure.None;
    }

    private bool CreateTray()
    {
        if (Application.Current is not Application app || _window.TryGetPlatformHandle() is not
            { HandleDescriptor: "HWND" })
        {
            return false;
        }

        NativeMenu menu = new NativeMenu();
        NativeMenuItem open = new NativeMenuItem(Strings.Get("ShowPostfold"));
        open.Click += (_, _) => Restore();
        NativeMenuItem exit = new NativeMenuItem(Strings.Get("ExitPostfold"));
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(open);
        menu.Items.Add(exit);
        _tray = new TrayIcon
        {
            Icon = _window.Icon,
            Menu = menu,
            ToolTipText = "Postfold",
            IsVisible = false
        };
        _tray.Clicked += (_, _) => Restore();
        TrayIcons icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
        TrayIcon.SetIcons(app, icons);
        icons.Add(_tray);
        if (_tray.NativeMenuExporter is null)
        {
            icons.Remove(_tray);
            _tray.Dispose();
            _tray = null;
            return false;
        }

        return true;
    }
}

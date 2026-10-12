using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Postfold.Core;
using Postfold.UI;
using SkiaSharp;

namespace Postfold.Desktop;

public sealed class DesktopIntegration : IDesktopIntegration, IDisposable
{
    private readonly Window _window;
    private readonly IDesktopPreferenceStore _store;
    private readonly INewMailNotification _notifications;
    private readonly SemaphoreSlim _changes = new SemaphoreSlim(1, 1);
    private DesktopPreferences _preferences = DesktopPreferences.Default;
    private TrayIcon? _tray;
    private bool _disposed;

    public DesktopIntegration(Window window, IDesktopPreferenceStore store, INewMailNotification notifications)
    {
        _window = window;
        _store = store;
        _notifications = notifications;
    }

    public event EventHandler? ExitRequested;
    public bool TrayEnabled => !_disposed && _tray is not null && _preferences.TrayEnabled;

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
        using SKBitmap bitmap = new SKBitmap(16, 16);
        using SKCanvas canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DimGray);
        using SKPaint paint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(2, 4, 12, 9, paint);
        canvas.DrawLine(2, 4, 8, 9, paint);
        canvas.DrawLine(8, 9, 14, 4, paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using MemoryStream source = new MemoryStream(data.ToArray(), false);
        // Temporary development placeholder; release branding remains separately reviewed.
        _tray = new TrayIcon { Icon = new WindowIcon(source), Menu = menu, ToolTipText = "Postfold", IsVisible = false };
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

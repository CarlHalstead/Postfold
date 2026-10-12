using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Postfold.Core;
using Postfold.UI;

namespace Postfold.Desktop;

/// <summary>Generic transient shell notification, with no registration, message data or persistent tray behaviour.</summary>
public sealed class WindowsNewMailNotification : INewMailNotification, IDisposable
{
    private readonly Window _window;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
    private IntPtr _library;
    private NotifyIcon? _notify;
    private WindowsNotificationData _data;
    private DateTimeOffset? _lastShown;
    private bool _added;
    private bool _disposed;

    public WindowsNewMailNotification(Window window, TimeProvider time)
    {
        _window = window;
        _time = time;
        _expiry.Tick += (_, _) => Clear();
    }

    public Failure ShowGeneric()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || !OperatingSystem.IsWindows() || _window.TryGetPlatformHandle() is not
            { HandleDescriptor: "HWND" } handle)
        {
            return Failure.Unavailable;
        }

        if (_lastShown is DateTimeOffset previous && _time.GetUtcNow() - previous < TimeSpan.FromSeconds(30))
        {
            return Failure.None;
        }

        if (_notify is null)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!NativeLibrary.TryLoad(Path.Combine(system, "shell32.dll"), out _library))
            {
                return Failure.Unavailable;
            }

            _notify = Marshal.GetDelegateForFunctionPointer<NotifyIcon>(NativeLibrary.GetExport(_library, "Shell_NotifyIconW"));
        }

        Clear();
        _data = new WindowsNotificationData
        {
            Size = (uint)Marshal.SizeOf<WindowsNotificationData>(),
            Window = handle.Handle,
            Id = 0x50464E,
            Flags = 0x04 | 0x10 | 0x40,
            Tooltip = "Postfold",
            Title = "Postfold",
            Information = Strings.Get("GenericNewMail"),
            InformationFlags = 0x01 | 0x80
        };
        if (!_notify(0, ref _data))
        {
            return Failure.Unavailable;
        }

        _added = true;
        _lastShown = _time.GetUtcNow();
        _expiry.Start();
        return Failure.None;
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _notify = null;
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }

    private void Clear()
    {
        _expiry.Stop();
        if (_added && _notify is not null)
        {
            _notify(2, ref _data);
            _added = false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool NotifyIcon(uint operation, ref WindowsNotificationData data);
}

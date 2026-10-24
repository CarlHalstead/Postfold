using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace Postfold.Desktop.Services.Updates;

/// <summary>Uses Windows' aggregate network-cost hint; other platforms retain conservative unknown cost.</summary>
internal sealed class SystemUpdateConnection : IUpdateConnection, IDisposable
{
    private readonly ConnectivityChanged? _nativeCallback;
    private readonly IntPtr _notification;
    private int _disposed;

    internal SystemUpdateConnection()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        if (OperatingSystem.IsWindows())
        {
            _nativeCallback = OnConnectivityChanged;
            if (NotifyNetworkConnectivityHintChange(_nativeCallback, IntPtr.Zero, false, out _notification) != 0)
            {
                _notification = IntPtr.Zero;
            }
        }
    }

    public UpdateConnection Current
    {
        get
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                return UpdateConnection.Offline;
            }
            if (!OperatingSystem.IsWindows() || Volatile.Read(ref _disposed) != 0 || _notification == IntPtr.Zero || GetNetworkConnectivityHint(out ConnectivityHint hint) != 0)
            {
                return UpdateConnection.Unknown;
            }
            return Interpret(hint.Level, hint.Cost, hint.ApproachingLimit != 0 || hint.OverLimit != 0 || hint.Roaming != 0);
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        if (_notification != IntPtr.Zero)
        {
            // The scheduler unsubscribes and disposes outside its gate. Cancellation waits for native
            // callbacks, so it must never be called from a callback or while holding their resources.
            CancelMibChangeNotify2(_notification);
            GC.KeepAlive(_nativeCallback);
        }
    }

    internal static UpdateConnection Interpret(int level, int cost, bool constrained)
    {
        if (level is 1 or 5)
        {
            return UpdateConnection.Offline;
        }
        if (level is not (2 or 3 or 4) || cost is not (1 or 2 or 3))
        {
            return UpdateConnection.Unknown;
        }
        return cost == 1 && !constrained ? UpdateConnection.Unmetered : UpdateConnection.Metered;
    }

    private void OnConnectivityChanged(IntPtr context, ConnectivityHint hint)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddressChanged(object? sender, EventArgs args)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetNetworkConnectivityHint(out ConnectivityHint hint);

    [DllImport("iphlpapi.dll")]
    private static extern uint NotifyNetworkConnectivityHintChange(ConnectivityChanged callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr notification);

    [DllImport("iphlpapi.dll")]
    private static extern uint CancelMibChangeNotify2(IntPtr notification);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ConnectivityChanged(IntPtr context, ConnectivityHint hint);

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectivityHint
    {
        internal int Level;
        internal int Cost;
        internal byte ApproachingLimit;
        internal byte OverLimit;
        internal byte Roaming;
    }
}

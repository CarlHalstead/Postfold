using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using System;
using System.Net.NetworkInformation;

namespace Postfold.Desktop.Services.Updates;

/// <summary>Availability is a hint; without a qualified native cost probe, online cost remains unknown.</summary>
internal sealed class SystemUpdateConnection : IUpdateConnection, IDisposable
{
    internal SystemUpdateConnection()
    {
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    public UpdateConnection Current => NetworkInterface.GetIsNetworkAvailable() ? UpdateConnection.Unknown : UpdateConnection.Offline;

    public event EventHandler? Changed;

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddressChanged(object? sender, EventArgs args)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

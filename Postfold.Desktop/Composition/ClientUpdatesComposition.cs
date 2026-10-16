using Postfold.Core.Interfaces;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System;
using System.Net.Http;

namespace Postfold.Desktop.Composition;

/// <summary>No production configuration is shipped. Only explicit configuration can create an active updater.</summary>
internal static class ClientUpdatesComposition
{
    internal static IClientUpdates Create(
        ClientUpdateConfiguration? configuration = null,
        TimeProvider? time = null,
        HttpMessageHandler? fixtureHandler = null,
        IDesktopPreferenceStore? preferences = null,
        IUpdateConnection? connection = null
    )
    {
        if (configuration is null)
        {
            return new InactiveClientUpdates();
        }
        HttpsUpdateTransport transport = new HttpsUpdateTransport(configuration.Release, time ?? TimeProvider.System, fixtureHandler);
        try
        {
            AuthenticatedClientUpdates client = new AuthenticatedClientUpdates(
                transport,
                configuration.PinnedRoot,
                configuration.Channel,
                configuration.InstalledVersion,
                configuration.ProfileFormat,
                configuration.Migration,
                configuration.StateDirectory
            );
            return preferences is null ? client : new ScheduledClientUpdates(
                client,
                preferences,
                connection ?? new SystemUpdateConnection(),
                time ?? TimeProvider.System,
                configuration.StateDirectory
            );
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}

using Postfold.Core.Interfaces;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System;
using System.Net.Http;

namespace Postfold.Desktop.Composition;

/// <summary>No production configuration is shipped. Supplying explicit configuration admits manual operations only.</summary>
internal static class ClientUpdatesComposition
{
    internal static IClientUpdates Create(ClientUpdateConfiguration? configuration = null, TimeProvider? time = null, HttpMessageHandler? fixtureHandler = null)
    {
        if (configuration is null)
        {
            return new InactiveClientUpdates();
        }
        HttpsUpdateTransport transport = new HttpsUpdateTransport(configuration.Release, time ?? TimeProvider.System, fixtureHandler);
        try
        {
            return new AuthenticatedClientUpdates(
                transport,
                configuration.PinnedRoot,
                configuration.Channel,
                configuration.InstalledVersion,
                configuration.ProfileFormat,
                configuration.Migration,
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

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.NUnit;
using NSubstitute;
using NUnit.Framework;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using Postfold.Engine.Tests.Fixtures;
using Postfold.Foundation.Tests.Fixtures.Updates;
using Postfold.UI.Views;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Tests.Updates;

public sealed class ProductionPreparationTests
{
    [TestCase("stable", false, UpdateStage.Ready)]
    [TestCase("beta", false, UpdateStage.Failed)]
    [TestCase("stable", true, UpdateStage.Failed)]
    public async Task Catalogue_WhenUnsignedLocationSelectsSignedArtifacts_StillRequiresPinnedKeyAndChannel(string channel, bool wrongKey, UpdateStage expected)
    {
        // Arrange
        using ClientUpdateFixture fixture = new ClientUpdateFixture();
        fixture.Documents["win-x64-beta.json"] = Encoding.UTF8.GetBytes("{\"release\":\"v1.1.0\",\"bridges\":{}}");
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(new Uri(ProductionUpdateIdentity.Discovery), fixture.Clock, fixture, channel);
        using AuthenticatedClientUpdates service = new AuthenticatedClientUpdates(transport, wrongKey ? ProductionUpdateIdentity.PublicKey : fixture.Signer.PublicKey, channel, "1.0.0", 2, UpdateFixture.InitialMigration, fixture.Directory);

        // Act
        await service.CheckAsync(new Progress<Postfold.Core.Models.Updates.UpdateStatus>(), CancellationToken.None);
        if (service.Current.Stage == UpdateStage.Available)
        {
            await service.DownloadAsync(new Progress<Postfold.Core.Models.Updates.UpdateStatus>(), CancellationToken.None);
        }

        // Assert
        Assert.That(service.Current.Stage, Is.EqualTo(expected));
        Assert.That(service.Current.CanRestart, Is.False);
        Assert.That(fixture.Requests.Contains(ClientUpdateFixture.BundleName), Is.EqualTo(expected == UpdateStage.Ready));
    }

    [Test]
    public void Identity_WhenReviewed_IsCanonicalAndUnconfiguredCompositionIsInactive()
    {
        // Arrange / Act
        ProductionUpdateIdentity.Validate();
        InactiveClientUpdates updates = (InactiveClientUpdates)ClientUpdatesComposition.Create();

        // Assert
        Assert.That(UpdateTrust.KeyId(ProductionUpdateIdentity.PublicKey), Is.EqualTo(ProductionUpdateIdentity.KeyId));
        Assert.That(updates.Current.CanRestart, Is.False);
    }

    [AvaloniaTest]
    [TestCase(false, UpdateStage.Inactive)]
    [TestCase(true, UpdateStage.Failed)]
    public async Task Startup_WhenUpdatesCannotActivate_AdmitsMailWithoutScheduler(bool invalidState, UpdateStage expected)
    {
        // Arrange
        string profiles = Path.Combine(EngineFixture.DirectoryPath(), "profiles");
        IClassicDesktopStyleApplicationLifetime lifetime = Substitute.For<IClassicDesktopStyleApplicationLifetime>();
        TaskCompletionSource shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime.When(value => value.Shutdown(0)).Do(_ => shutdown.TrySetResult());
        ClientUpdateConfiguration? configuration = invalidState ? new ClientUpdateConfiguration(
            new Uri(ProductionUpdateIdentity.Discovery),
            ProductionUpdateIdentity.PublicKey,
            "beta",
            "1.0.0",
            2,
            UpdateFixture.InitialMigration,
            AppContext.BaseDirectory
        ) : null;
        DesktopSession session = new DesktopSession(lifetime, profilesDirectory: profiles, updateConfiguration: configuration);
        session.Window.Show();
        try
        {
            // Act
            await session.Startup;
            IClientUpdates updates = (IClientUpdates)typeof(DesktopSession).GetField("_updates", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;

            // Assert
            Assert.That(updates, Is.TypeOf<InactiveClientUpdates>());
            Assert.That(updates.Current.Stage, Is.EqualTo(expected));
            Assert.That(updates.Current.Failure, Is.EqualTo(invalidState ? Failure.NeedsReview : Failure.None));
            Assert.That(typeof(DesktopSession).GetField("_updateConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session), Is.EqualTo(configuration));
            Assert.That(session.Window.OwnedWindows, Has.Some.TypeOf<AccountSetupWindow>());
        }
        finally
        {
            foreach (Window child in session.Window.OwnedWindows.ToArray())
            {
                child.Close();
            }
            session.Window.Close();
            await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            session.Window.Close();
        }
    }

    [Test]
    public async Task InactiveClient_WhenAdmissionFailed_RetainsActionableFailureWithoutRestart()
    {
        // Arrange
        InactiveClientUpdates updates = new InactiveClientUpdates(Failure.NeedsReview);

        // Act
        UpdateStatus status = await updates.CheckAsync(new Progress<UpdateStatus>(), CancellationToken.None);
        UpdateStatus restart = await updates.RestartAsync(CancellationToken.None);

        // Assert
        Assert.That(status.Stage, Is.EqualTo(UpdateStage.Failed));
        Assert.That(status.Failure, Is.EqualTo(Failure.NeedsReview));
        Assert.That(restart.CanRestart, Is.False);
        Assert.That(restart, Is.EqualTo(status));
    }

    [TestCase("{\"release\":null,\"bridges\":{}}", Failure.NotFound)]
    [TestCase("{\"release\":\"../latest\",\"bridges\":{}}", Failure.Validation)]
    [TestCase("{\"release\":\"https://other.invalid/\",\"bridges\":{}}", Failure.Validation)]
    [TestCase("{\"release\":\"v1\",\"bridges\":{},\"key\":\"unsigned\"}", Failure.Validation)]
    [TestCase("{\"release\":\"v1\",\"release\":\"v2\",\"bridges\":{}}", Failure.Validation)]
    [TestCase("{\"release\":\"v1\",\"bridges\":{\"unknown\":\"v0\"}}", Failure.Validation)]
    public async Task Catalogue_WhenEmptyOrMalformed_RefusesWithoutAssetRequest(string catalogue, Failure expected)
    {
        // Arrange
        Handler handler = new Handler(_ => catalogue);
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(new Uri(ProductionUpdateIdentity.Discovery), TimeProvider.System, handler);

        // Act
        Failure result = await transport.DiscoverLocationAsync(CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(expected));
        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task Catalogue_WhenRefreshed_RoutesNewReleasesAndRetainedBridgesWithoutReusingOldBytes()
    {
        // Arrange
        string transition = "transition-" + new string('A', 64);
        string tag = "v1";
        Handler handler = new Handler(uri => uri.AbsoluteUri == ProductionUpdateIdentity.Discovery ?
            "{\"release\":\"" + tag + "\",\"bridges\":{\"" + transition + "\":\"retained-v0\"}}" : uri.AbsolutePath);
        using HttpsUpdateTransport transport = new HttpsUpdateTransport(new Uri(ProductionUpdateIdentity.Discovery), TimeProvider.System, handler);

        // Act
        Assert.That(await transport.DiscoverLocationAsync(CancellationToken.None), Is.EqualTo(Failure.None));
        Outcome<byte[]> first = await transport.FetchAsync("Postfold.win-x64-beta.manifest.json", 65536, CancellationToken.None);
        Outcome<byte[]> bridge = await transport.FetchAsync(transition + ".sig", 65536, CancellationToken.None);
        tag = "v2";
        Assert.That(await transport.DiscoverLocationAsync(CancellationToken.None), Is.EqualTo(Failure.None));
        Outcome<byte[]> second = await transport.FetchAsync("Postfold.win-x64-beta.manifest.json", 65536, CancellationToken.None);

        // Assert
        Assert.That(Encoding.UTF8.GetString(first.Value!), Is.EqualTo("/CarlHalstead/Postfold/main/updates/releases/v1/win-x64-beta/Postfold.win-x64-beta.manifest.json"));
        Assert.That(Encoding.UTF8.GetString(second.Value!), Is.EqualTo("/CarlHalstead/Postfold/main/updates/releases/v2/win-x64-beta/Postfold.win-x64-beta.manifest.json"));
        Assert.That(Encoding.UTF8.GetString(bridge.Value!), Is.EqualTo("/CarlHalstead/Postfold/main/updates/releases/retained-v0/win-x64-beta/" + transition + ".sig"));
        Assert.That(UpdateTrust.VerifyManifest(second.Value!, new byte[384], ProductionUpdateIdentity.PublicKey).IsSuccess, Is.False);
    }

    [TestCase("broker")]
    [TestCase("helper")]
    [TestCase("missing")]
    [TestCase("source")]
    [TestCase("version")]
    [TestCase("valid")]
    public void Inventory_WhenReviewed_BindsExactNativeBytesAndInstalledIdentity(string variation)
    {
        // Arrange
        string installation = Path.Combine(TestContext.CurrentContext.WorkDirectory, "production-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(installation, "current"));
        byte[] broker = Encoding.UTF8.GetBytes("synthetic inventory broker");
        byte[] helper = Encoding.UTF8.GetBytes("synthetic inventory helper");
        string brokerPath = Path.Combine(installation, "current", "Postfold.UpdateBroker.exe");
        File.WriteAllBytes(brokerPath, broker);
        File.WriteAllBytes(Path.Combine(installation, "Update.exe"), helper);
        File.WriteAllText(Path.Combine(installation, "current", "sq.version"), "<package><metadata><id>Postfold</id><version>" + (variation == "version" ? "1.0.1" : "1.0.0") + "</version></metadata></package>");
        string source = new string('a', 40);
        using MemoryStream inventory = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            product = "Postfold",
            version = "1.0.0",
            sourceCommit = source,
            keyId = ProductionUpdateIdentity.KeyId,
            channel = "beta",
            rid = "win-x64",
            brokerSha256 = Convert.ToHexString(SHA256.HashData(broker)),
            helperSha256 = Convert.ToHexString(SHA256.HashData(helper))
        }));
        if (variation == "broker")
        {
            File.WriteAllText(brokerPath, "changed");
        }
        if (variation == "helper")
        {
            File.WriteAllText(Path.Combine(installation, "Update.exe"), "changed");
        }
        if (variation == "missing")
        {
            File.Move(brokerPath, brokerPath + ".retained");
        }

        // Act / Assert
        if (variation == "valid")
        {
            (ClientUpdateConfiguration client, NativeRestartConfiguration native) = ProductionUpdatesPreparation.ReviewInventory(inventory, installation, "1.0.0+" + source, 2, "20261014150754_InitialEfProfile");
            Assert.That(client.InstalledVersion, Is.EqualTo("1.0.0"));
            Assert.That(client.Channel, Is.EqualTo("beta"));
            Assert.That(client.StateDirectory, Does.EndWith(Path.Combine("Postfold", "updates", "win-x64", "beta")));
            Assert.That(native.BrokerSha256, Is.EqualTo(Convert.ToHexString(SHA256.HashData(broker))));
        }
        else
        {
            Assert.That(() => ProductionUpdatesPreparation.ReviewInventory(inventory, installation, "1.0.0+" + (variation == "source" ? new string('b', 40) : source), 2, "20261014150754_InitialEfProfile"), variation == "missing" ? Throws.InstanceOf<FileNotFoundException>() : Throws.InstanceOf<InvalidDataException>());
        }
    }

    private sealed class Handler(Func<Uri, string> response) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request.RequestUri!))
            });
        }
    }
}

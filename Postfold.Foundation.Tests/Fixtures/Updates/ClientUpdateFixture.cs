using Avalonia.Controls.ApplicationLifetimes;
using NUnit.Framework;
using Postfold.Core.Interfaces;
using Postfold.Desktop.Composition;
using Postfold.Desktop.Interfaces;
using Postfold.Desktop.Models.Updates;
using Postfold.Desktop.Services.Updates;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

/// <summary>Ephemeral signing keys and intercepted HTTP only; no test root is compiled into Desktop.</summary>
internal sealed class ClientUpdateFixture : HttpMessageHandler
{
    private readonly UpdateFixture _signer = new UpdateFixture();
    private readonly Dictionary<string, byte[]> _initialDocuments;
    private bool _disposed;

    internal ClientUpdateFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "Postfold-U02-" + Guid.NewGuid().ToString("N"));
        byte[] manifest = _signer.Manifest();
        Documents["Postfold.win-x64-stable.manifest.json"] = manifest;
        Documents["Postfold.win-x64-stable.manifest.sig"] = _signer.Sign(manifest);
        Documents["releases.win-x64-stable.json"] = _signer.Feed;
        Documents[PackageName] = _signer.Package;
        _initialDocuments = new Dictionary<string, byte[]>(Documents, StringComparer.Ordinal);
    }

    internal const string PackageName = "Postfold-1.1.0-win-x64-stable-full.nupkg";
    internal string Directory { get; }
    internal UpdateFixture Signer => _signer;
    internal Dictionary<string, byte[]> Documents { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    internal List<string> Requests { get; } = [];
    internal Func<string, CancellationToken, Task<HttpResponseMessage>>? Response { get; set; }
    internal FixtureClock Clock { get; } = new FixtureClock();

    internal AuthenticatedClientUpdates Create(string? root = null, string version = "1.0.0", int format = 2, string migration = UpdateFixture.InitialMigration)
    {
        return (AuthenticatedClientUpdates)ClientUpdatesComposition.Create(Configuration(root, version, format, migration), Clock, new ForwardingHandler(this));
    }

    internal ScheduledClientUpdates CreateScheduled(IDesktopPreferenceStore preferences, IUpdateConnection connection, TimeProvider clock)
    {
        return (ScheduledClientUpdates)ClientUpdatesComposition.Create(
            Configuration(null, "1.0.0", 2, UpdateFixture.InitialMigration),
            clock,
            new ForwardingHandler(this),
            preferences,
            connection
        );
    }

    internal DesktopSession CreateSession(IClassicDesktopStyleApplicationLifetime lifetime, string profilesDirectory, TimeProvider time, IUpdateConnection connection)
    {
        return new DesktopSession(
            lifetime,
            profilesDirectory: profilesDirectory,
            updateConfiguration: Configuration(null, "1.0.0", 2, UpdateFixture.InitialMigration),
            updateTime: time,
            updateHandler: new ForwardingHandler(this),
            updateConnection: connection
        );
    }

    private ClientUpdateConfiguration Configuration(string? root, string version, int format, string migration)
    {
        return new ClientUpdateConfiguration(
            new Uri("https://github.com/CarlHalstead/Postfold/releases/download/synthetic-u02/"),
            root ?? _signer.PublicKey,
            "stable",
            version,
            format,
            migration,
            Directory
        );
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
        Requests.Add(name);
        cancellationToken.ThrowIfCancellationRequested();
        return Response?.Invoke(name, cancellationToken) ?? Task.FromResult(Respond(name));
    }

    protected override void Dispose(bool disposing)
    {
        // Tests may reopen the real service over the same isolated responses and durable directory.
        if (disposing && !_disposed)
        {
            _disposed = true;
            string evidence = Path.Combine(Directory, "fixture-evidence");
            System.IO.Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "synthetic-root.public-spki"), _signer.PublicKey);
            List<object> artifacts = [];
            foreach ((string phase, Dictionary<string, byte[]> documents) in new[] { ("initial", _initialDocuments), ("final", Documents) })
            {
                foreach (KeyValuePair<string, byte[]> document in documents)
                {
                    string path = Path.Combine(evidence, phase + "-" + document.Key);
                    File.WriteAllBytes(path, document.Value);
                    artifacts.Add(new { path, length = document.Value.Length, sha256 = UpdateFixture.Digest(document.Value) });
                }
            }
            string receipt = Path.Combine(evidence, "receipt.json");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new
            {
                syntheticOnly = true,
                test = TestContext.CurrentContext.Test.FullName,
                rootKeyId = _signer.KeyId,
                profile = Directory,
                artifacts,
                requests = Requests
            }));
            TestContext.AddTestAttachment(receipt, "Synthetic U02 fixture identities; no private key retained");
            _signer.Dispose();
        }
        base.Dispose(disposing);
    }

    internal HttpResponseMessage Respond(string name)
    {
        return Documents.TryGetValue(name, out byte[]? bytes) ? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        } : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    internal sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private sealed class ForwardingHandler(ClientUpdateFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return fixture.SendAsync(request, cancellationToken);
        }
    }
}

using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Desktop.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

internal sealed class FixtureUpdateTransport : IUpdateTransport
{
    internal Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    internal byte[] Package { get; set; } = [];
    internal int Downloads { get; private set; }
    internal Action? DuringDownload { get; set; }

    public Task<Outcome<byte[]>> FetchAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Files.TryGetValue(name, out byte[]? bytes) && bytes.Length <= maximumBytes ?
            Outcome<byte[]>.Success((byte[])bytes.Clone()) : Outcome<byte[]>.Fail(Failure.NotFound));
    }

    public async Task<Failure> DownloadAsync(UpdateArtifact artifact, Stream destination, CancellationToken cancellationToken)
    {
        Downloads++;
        await destination.WriteAsync(Package, cancellationToken);
        DuringDownload?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Failure.None;
    }

    internal static FixtureUpdateTransport Create(UpdateFixture fixture)
    {
        FixtureUpdateTransport transport = new FixtureUpdateTransport
        {
            Package = fixture.Package
        };
        byte[] manifest = fixture.Manifest();
        transport.Files["Postfold.win-x64-stable.manifest.json"] = manifest;
        transport.Files["Postfold.win-x64-stable.manifest.sig"] = fixture.Sign(manifest);
        transport.Files["releases.win-x64-stable.json"] = fixture.Feed;
        return transport;
    }
}

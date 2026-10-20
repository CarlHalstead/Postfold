using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Desktop.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Foundation.Tests.Fixtures.Updates;

internal sealed class FixtureUpdateTransport : IUpdateTransport
{
    internal Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    internal byte[] Package { get; set; } = [];
    internal byte[]? BundleBytes { get; set; }
    internal int Downloads { get; private set; }
    internal Action? DuringDownload { get; set; }

    public Task<Outcome<byte[]>> FetchAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Files.TryGetValue(name, out byte[]? bytes) && bytes.Length <= maximumBytes ?
            Outcome<byte[]>.Success((byte[])bytes.Clone()) : Outcome<byte[]>.Fail(Failure.NotFound));
    }

    public async Task<Failure> DownloadBundleAsync(string name, long maximumBytes, Stream destination, CancellationToken cancellationToken)
    {
        Downloads++;
        Dictionary<string, byte[]> entries = new Dictionary<string, byte[]>(Files, StringComparer.Ordinal)
        {
            ["Postfold-1.1.0-win-x64-stable-full.nupkg"] = Package
        };
        await destination.WriteAsync(BundleBytes ?? Bundle(entries), cancellationToken);
        DuringDownload?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Failure.None;
    }

    internal static byte[] Bundle(Dictionary<string, byte[]> entries, CompressionLevel compression = CompressionLevel.Optimal)
    {
        using MemoryStream bytes = new MemoryStream();
        using (ZipArchive archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (KeyValuePair<string, byte[]> entry in entries)
            {
                using Stream output = archive.CreateEntry(entry.Key, compression).Open();
                output.Write(entry.Value);
            }
        }
        return bytes.ToArray();
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

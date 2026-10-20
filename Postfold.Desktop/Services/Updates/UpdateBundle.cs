using Postfold.Core.Enums;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Desktop.Services.Updates;

/// <summary>
/// The owned distribution subset: bounded classic, single-disk, flat stored/deflate ZIPs without
/// descriptors. Preflight precedes runtime directory allocation; only selected entries are opened.
/// </summary>
internal static class UpdateBundle
{
    internal const long MaximumBytes = 536870912;
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static async Task<Failure> StageAsync(FileStream bundle, Stream package, VerifiedUpdateSource selection, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => Preflight(bundle, selection.Manifest, cancellationToken), cancellationToken).ConfigureAwait(false);
            bundle.Position = 0;
            using ZipArchive archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
            string prefix = "Postfold.win-x64-" + selection.Manifest.Channel + ".manifest.";
            await CompareAsync(archive, prefix + "json", selection.Document, cancellationToken).ConfigureAwait(false);
            await CompareAsync(archive, prefix + "sig", selection.Signature, cancellationToken).ConfigureAwait(false);
            await CompareAsync(archive, selection.Manifest.Feed.Name, selection.Feed, cancellationToken).ConfigureAwait(false);
            ZipArchiveEntry entry = archive.GetEntry(selection.Manifest.Package.Name) ?? throw new InvalidDataException("Missing signed package.");
            Require(entry.Length == selection.Manifest.Package.Length);
            using CrcReadStream source = new CrcReadStream(entry.Open());
            Failure copied = await UpdateArtifacts.CopyVerifiedAsync(source, package, selection.Manifest.Package, cancellationToken).ConfigureAwait(false);
            return copied == Failure.None && source.Crc == entry.Crc32 ? Failure.None : Failure.Validation;
        }
        catch (InvalidDataException)
        {
            return Failure.Validation;
        }
    }

    /// <summary>Examines at most 64 KiB of directory and bounded headers, never compressed content.</summary>
    internal static void Preflight(Stream stream, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        Require(stream.CanSeek && stream.Length is >= 22 and <= MaximumBytes);
        // The tail buffer includes the maximum classic comment. Only an exact end record is accepted.
        byte[] tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        stream.ReadExactly(tail);
        int end = -1;
        for (int index = tail.Length - 22; index >= 0; index--)
        {
            if (U32(tail, index) == 0x06054b50 && index + 22 + U16(tail, index + 20) == tail.Length)
            {
                Require(end == -1);
                end = index;
            }
        }
        Require(end >= 0);
        int count = U16(tail, end + 10);
        long directoryOffset = U32(tail, end + 16);
        Require(U32(tail, end + 12) <= 65536);
        int directoryLength = (int)U32(tail, end + 12);
        Require(U16(tail, end + 4) == 0 && U16(tail, end + 6) == 0 &&
            count is > 0 and <= 32 && U16(tail, end + 8) == count &&
            directoryLength is > 0 and <= 65536 && directoryOffset + directoryLength == stream.Length - tail.Length + end);
        byte[] directory = new byte[directoryLength];
        stream.Position = directoryOffset;
        stream.ReadExactly(directory);
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<(long Start, long End)> ranges = [];
        long total = 0;
        int position = 0;
        int localMaterial = 0;
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(position <= directory.Length - 46 && U32(directory, position) == 0x02014b50);
            int flags = U16(directory, position + 8);
            int method = U16(directory, position + 10);
            uint compressed = U32(directory, position + 20);
            uint length = U32(directory, position + 24);
            int nameLength = U16(directory, position + 28);
            int extraLength = U16(directory, position + 30);
            int commentLength = U16(directory, position + 32);
            uint attributes = U32(directory, position + 38);
            long offset = U32(directory, position + 42);
            int next = checked(position + 46 + nameLength + extraLength + commentLength);
            Require(next <= directory.Length && nameLength is > 0 and <= 128 &&
                U16(directory, position + 6) <= 20 && (flags & ~0x0800) == 0 && method is 0 or 8 &&
                compressed != uint.MaxValue && length != uint.MaxValue && U16(directory, position + 34) == 0 &&
                (attributes & 0xffff & ~0x27u) == 0 && ((attributes >> 16) & 0xf000) is 0 or 0x8000);
            if (method == 0)
            {
                Require(compressed == length);
            }
            ReadOnlySpan<byte> encodedName = directory.AsSpan(position + 46, nameLength);
            foreach (byte character in encodedName)
            {
                Require(character is >= 0x21 and <= 0x7e);
            }
            string name = Encoding.ASCII.GetString(encodedName);
            Require(ApprovedName(name, manifest) && names.Add(name));
            CheckExtras(directory.AsSpan(position + 46 + nameLength, extraLength));
            total = checked(total + length);
            Require(total <= 2147483648L && offset <= directoryOffset - 30);
            byte[] local = new byte[30];
            stream.Position = offset;
            stream.ReadExactly(local);
            int localName = U16(local, 26);
            int localExtra = U16(local, 28);
            localMaterial = checked(localMaterial + 30 + localName + localExtra);
            Require(localMaterial <= 65536 && U32(local, 0) == 0x04034b50 &&
                U16(local, 4) == U16(directory, position + 6) && U16(local, 6) == flags && U16(local, 8) == method &&
                U32(local, 10) == U32(directory, position + 12) && U32(local, 14) == U32(directory, position + 16) &&
                U32(local, 18) == compressed && U32(local, 22) == length && localName == nameLength);
            long dataEnd = checked(offset + 30 + localName + localExtra + compressed);
            Require(dataEnd <= directoryOffset);
            byte[] material = new byte[localName + localExtra];
            stream.ReadExactly(material);
            Require(material.AsSpan(0, localName).SequenceEqual(encodedName));
            CheckExtras(material.AsSpan(localName));
            ranges.Add((offset, dataEnd));
            position = next;
        }
        Require(position == directory.Length);
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        long previous = 0;
        foreach ((long start, long finish) in ranges)
        {
            Require(start == previous);
            previous = finish;
        }
        Require(previous == directoryOffset && names.Contains(manifest.Package.Name) && names.Contains(manifest.Feed.Name) &&
            names.Contains("Postfold.win-x64-" + manifest.Channel + ".manifest.json") && names.Contains("Postfold.win-x64-" + manifest.Channel + ".manifest.sig"));
    }

    private static async Task CompareAsync(ZipArchive archive, string name, ImmutableArray<byte> expected, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException("Missing selected metadata.");
        Require(entry.Length == expected.Length);
        byte[] bytes = new byte[expected.Length];
        using CrcReadStream source = new CrcReadStream(entry.Open());
        await source.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        byte[] excess = new byte[1];
        Require(await source.ReadAsync(excess, cancellationToken).ConfigureAwait(false) == 0 &&
            bytes.AsSpan().SequenceEqual(expected.AsSpan()) && source.Crc == entry.Crc32);
    }

    private static bool ApprovedName(string name, UpdateManifest manifest)
    {
        string channel = "win-x64-" + manifest.Channel;
        return name == manifest.Package.Name || name == manifest.Feed.Name ||
            name == "Postfold." + channel + ".manifest.json" || name == "Postfold." + channel + ".manifest.sig" ||
            name == "Postfold-" + channel + "-Setup.exe" || name is "README.txt" or "SHA256SUMS.txt" ||
            name == "assets." + channel + ".json" || name == "RELEASES-" + channel ||
            Regex.IsMatch(name, @"\A(transition|activation)-[0-9A-F]{64}\.(json|sig)\z", RegexOptions.CultureInvariant);
    }

    private static void CheckExtras(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            Require(bytes.Length >= 4);
            int length = U16(bytes, 2);
            Require(U16(bytes, 0) != 1 && length <= bytes.Length - 4);
            bytes = bytes[(length + 4)..];
        }
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static void Require(bool valid)
    {
        if (!valid)
        {
            throw new InvalidDataException("Unsupported or inconsistent update ZIP.");
        }
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            }
            table[index] = value;
        }
        return table;
    }

    // .NET 10 exposes the stored CRC but does not validate it on entry reads.
    private sealed class CrcReadStream(Stream source) : Stream
    {
        private uint _crc = uint.MaxValue;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        internal uint Crc => ~_crc;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < read; index++)
            {
                byte value = buffer.Span[index];
                _crc = (_crc >> 8) ^ CrcTable[(_crc ^ value) & 255];
            }
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

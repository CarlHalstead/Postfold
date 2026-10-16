using Postfold.Core.Enums;
using Postfold.Core.Models;
using Postfold.Core.Models.Updates;
using Postfold.Core.Policies.Updates;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Postfold.Desktop.Stores;

/// <summary>
/// Exclusive append-only signed trust journal outside the installation. Call off the UI thread.
/// Any torn tail blocks trust rather than silently rolling back an acknowledged watermark or key.
/// </summary>
internal sealed class UpdateTrustStore : IDisposable
{
    private const long MaximumJournalBytes = 16777216;
    private readonly FileStream _journal;
    private bool _failed;

    private UpdateTrustStore(FileStream journal, UpdateTrustState state)
    {
        _journal = journal;
        State = state;
    }

    internal UpdateTrustState State { get; private set; }
    /// <summary>IPC fault-injection checkpoints for disposable tests; unset in application use.</summary>
    internal Action<string>? Checkpoint { get; set; }

    public void Dispose()
    {
        lock (_journal)
        {
            _failed = true;
            _journal.Dispose();
        }
    }

    internal static Outcome<UpdateTrustStore> Open(string path, string pinnedRoot, string channel, bool allowCreate = true)
    {
        FileStream? journal = null;
        try
        {
            if (channel is not ("stable" or "beta"))
            {
                return Outcome<UpdateTrustStore>.Fail(Failure.Validation);
            }
            byte[] header = Encoding.ASCII.GetBytes("PostfoldUpdateTrust/1:" + UpdateTrust.KeyId(pinnedRoot) + ":" + channel + "\n");
            path = Path.GetFullPath(path);
            VerifyPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path);
            if (!exists && !allowCreate)
            {
                throw new InvalidDataException("Native apply requires existing update trust evidence.");
            }
            if (!exists && (File.Exists(path + ".head") || Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".head.*.tmp"
            ).Any()))
            {
                throw new InvalidDataException("Missing update trust journal with retained checkpoint evidence.");
            }
            journal = new FileStream(path, exists ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            if (!exists)
            {
                journal.Write(header);
                journal.Flush(flushToDisk: true);
                WriteHead(journal);
            }
            if (journal.Length > MaximumJournalBytes)
            {
                throw new InvalidDataException("Update trust journal exceeds its bound.");
            }
            long checkpointLength = VerifyHead(journal);
            journal.Position = 0;
            byte[] actualHeader = new byte[header.Length];
            journal.ReadExactly(actualHeader);
            if (!actualHeader.AsSpan().SequenceEqual(header))
            {
                throw new InvalidDataException("Update trust journal identity mismatch.");
            }
            UpdateTrustState state = new UpdateTrustState(pinnedRoot, channel);
            using BinaryReader reader = new BinaryReader(journal, Encoding.UTF8, leaveOpen: true);
            while (journal.Position < journal.Length)
            {
                byte kind = reader.ReadByte();
                int length = reader.ReadInt32();
                if (length is <= 0 or > UpdateTrust.MaximumDocumentBytes || kind is not (1 or 2))
                {
                    throw new InvalidDataException("Update trust journal frame rejected.");
                }
                byte[] document = new byte[length];
                byte[] signature = new byte[UpdateTrust.SignatureLength];
                journal.ReadExactly(document);
                journal.ReadExactly(signature);
                Outcome<UpdateTrustState> accepted = kind == 1 ? state.AcceptManifest(document, signature) :
                    state.AcceptTransition(document, signature);
                state = accepted.Value ?? throw new InvalidDataException("Update trust journal authentication failed.");
            }
            if (journal.Length != checkpointLength)
            {
                // A flushed signed frame may survive a crash before its atomic head promotion.
                WriteHead(journal);
            }
            UpdateTrustStore result = new UpdateTrustStore(journal, state);
            journal = null;
            return Outcome<UpdateTrustStore>.Success(result);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or CryptographicException or FormatException)
        {
            return Outcome<UpdateTrustStore>.Fail(Failure.NeedsReview);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Outcome<UpdateTrustStore>.Fail(Failure.Unavailable);
        }
        finally
        {
            journal?.Dispose();
        }
    }

    internal Outcome<UpdateTrustState> Accept(ReadOnlyMemory<byte> document, ReadOnlySpan<byte> signature, bool transition = false)
    {
        if (document.Length is <= 0 or > UpdateTrust.MaximumDocumentBytes || signature.Length != UpdateTrust.SignatureLength)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.Validation);
        }
        byte[] ownedDocument = document.ToArray();
        byte[] ownedSignature = signature.ToArray();
        lock (_journal)
        {
            return AcceptOwned(ownedDocument, ownedSignature, transition);
        }
    }

    private Outcome<UpdateTrustState> AcceptOwned(ReadOnlyMemory<byte> document, ReadOnlySpan<byte> signature, bool transition)
    {
        if (_failed)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.NeedsReview);
        }
        Outcome<UpdateTrustState> accepted = transition ? State.AcceptTransition(document, signature) :
            State.AcceptManifest(document, signature);
        if (!accepted.IsSuccess)
        {
            return accepted;
        }
        if (accepted.Value == State)
        {
            return accepted;
        }
        // This bounded log deliberately has no automatic compaction: retain every rotation link.
        if (_journal.Length + 5 + document.Length + signature.Length > MaximumJournalBytes)
        {
            return Outcome<UpdateTrustState>.Fail(Failure.NeedsReview);
        }
        try
        {
            using BinaryWriter writer = new BinaryWriter(_journal, Encoding.UTF8, leaveOpen: true);
            writer.Write(transition ? (byte)2 : (byte)1);
            writer.Write(document.Length);
            writer.Write(document.Span);
            writer.Write(signature);
            writer.Flush();
            Checkpoint?.Invoke("frame-written");
            _journal.Flush(flushToDisk: true);
            Checkpoint?.Invoke("journal-flushed");
            WriteHead(_journal, Checkpoint);
            State = accepted.Value!;
            return accepted;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _failed = true;
            return Outcome<UpdateTrustState>.Fail(Failure.Unavailable);
        }
    }

    private static void VerifyPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Update trust paths must not use links.");
            }
        }
    }

    private static long VerifyHead(FileStream journal)
    {
        string path = journal.Name + ".head";
        VerifyPath(path);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("Missing update trust checkpoint.");
        }
        using FileStream head = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using BinaryReader reader = new BinaryReader(head);
        if (head.Length != 40)
        {
            throw new InvalidDataException("Malformed update trust checkpoint.");
        }
        long length = reader.ReadInt64();
        byte[] expected = reader.ReadBytes(32);
        if (length <= 0 || length > journal.Length)
        {
            throw new InvalidDataException("Truncated update trust journal.");
        }
        journal.Position = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long remaining = length;
        while (remaining > 0)
        {
            int read = journal.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
        {
            throw new InvalidDataException("Changed update trust journal.");
        }
        return length;
    }

    private static void WriteHead(FileStream journal, Action<string>? checkpoint = null)
    {
        string path = journal.Name + ".head";
        VerifyPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        journal.Position = 0;
        byte[] digest = SHA256.HashData(journal);
        using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using BinaryWriter writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(journal.Length);
            writer.Write(digest);
            writer.Flush();
            output.Flush(flushToDisk: true);
        }
        checkpoint?.Invoke("head-prepared");
        File.Move(temporary, path, overwrite: true);
        checkpoint?.Invoke("head-promoted");
    }
}

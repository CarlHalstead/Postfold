using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Postfold.Core;

namespace Postfold.Storage;

public sealed class BlobStore : IBlobStore
{
    private readonly string _root;

    public BlobStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        VerifyPath(_root);
    }

    public async Task<BlobReference> WriteAsync(AccountId account, Stream source, long maximumBytes,
        bool protect, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(account.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        VerifyPath(_root);
        Guid id = Guid.NewGuid();
        string temporary = Path.Combine(_root, id.ToString("N") + ".tmp");
        string final = ObjectPath(id);
        await File.WriteAllTextAsync(Path.Combine(_root, id.ToString("N") + ".owner"),
            JsonSerializer.Serialize(new BlobOwnership(account, protect)), cancellationToken).ConfigureAwait(false);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using (FileStream target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                length = checked(length + count);
                if (length > maximumBytes)
                {
                    // Interrupted files remain orphan candidates; no database reference has been committed.
                    throw new InvalidDataException("Content exceeds the configured resource limit.");
                }

                hash.AppendData(buffer, 0, count);
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        VerifyPath(_root);
        File.Move(temporary, final);
        return new BlobReference(id, account, length, Convert.ToHexString(hash.GetHashAndReset()), protect);
    }

    public async Task<Stream> OpenAsync(BlobReference blob, CancellationToken cancellationToken)
    {
        string path = ObjectPath(blob.Id);
        VerifyPath(path);
        FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (stream.Length != blob.Length || !CryptographicOperations.FixedTimeEquals(hash,
                Convert.FromHexString(blob.Sha256)))
            {
                throw new InvalidDataException("Stored object failed its integrity check.");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal string ObjectPath(Guid id)
    {
        return Path.Combine(_root, id.ToString("N") + ".blob");
    }

    internal void ValidateOwnership(BlobReference blob)
    {
        string path = Path.Combine(_root, blob.Id.ToString("N") + ".owner");
        VerifyPath(path);
        BlobOwnership ownership = JsonSerializer.Deserialize<BlobOwnership>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Missing object ownership.");
        if (ownership.Account != blob.Account || ownership.Protected != blob.Protected)
        {
            throw new InvalidDataException("Object ownership cannot be changed by a reference.");
        }
    }

    internal int CleanupDisposable(IReadOnlySet<Guid> referenced, DateTimeOffset olderThan)
    {
        VerifyPath(_root);
        int count = 0;
        foreach (string ownerPath in Directory.EnumerateFiles(_root, "*.owner", SearchOption.TopDirectoryOnly))
        {
            VerifyPath(ownerPath);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(ownerPath), "N", out Guid id) ||
                referenced.Contains(id) || File.GetLastWriteTimeUtc(ownerPath) >= olderThan.UtcDateTime)
            {
                continue;
            }

            BlobOwnership? ownership = JsonSerializer.Deserialize<BlobOwnership>(File.ReadAllText(ownerPath));
            if (ownership is null || ownership.Protected)
            {
                continue;
            }

            foreach (string suffix in new[] { ".tmp", ".blob" })
            {
                string candidate = Path.Combine(_root, id.ToString("N") + suffix);
                VerifyPath(candidate);
                File.Delete(candidate);
            }

            File.Delete(ownerPath);
            count++;
        }

        return count;
    }

    internal static void VerifyPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Profile paths cannot contain filesystem redirection.");
            }

            current = Path.GetDirectoryName(current);
        }
    }
}

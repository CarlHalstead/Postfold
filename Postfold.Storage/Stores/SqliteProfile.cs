using Microsoft.Data.Sqlite;
using Postfold.Core.Enums;
using Postfold.Core.Interfaces;
using Postfold.Core.Models;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Appearance;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Mailboxes;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Mime;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Search;
using Postfold.Core.Models.Synchronisation;
using Postfold.Core.Models.Updates;
using Postfold.Storage.Models;
using Postfold.Storage.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Storage.Stores;

public sealed class SqliteProfile : ISynchronisationStore, ILocalMailQuery, IOperationStore,
    IDraftStore, IOutboxStore, ICacheStore, IClientCatalog, IAppearanceStore, IAccountCapabilityStore,
    IDesktopPreferenceStore, IAccountIdentityStore, IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly BlobStore _blobs;
    private readonly SemaphoreSlim _writer = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _readers = new SemaphoreSlim(4, 4);
    private readonly TimeProvider _time;
    private int _disposed;

    private SqliteProfile(string path, TimeProvider time, FileStream lease)
    {
        _path = Path.GetFullPath(path);
        _time = time;
        _lease = lease;
        _blobs = new BlobStore(Path.Combine(Path.GetDirectoryName(_path)!, "objects"));
    }

    public IBlobStore Blobs => _blobs;

    public Task<AccountId> NextAccountIdAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection => new AccountId(Convert.ToInt64(
            Scalar(connection, null, "SELECT COALESCE(MAX(AccountId),0)+1 FROM Accounts;"),
            CultureInfo.InvariantCulture
        )), cancellationToken);
    }

    /// <summary>Retains local data and credential references; refuses to hide durable work needing a decision.</summary>
    public Task<Failure> DeactivateAccountAsync(AccountId account, CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM Accounts WHERE AccountId=$a AND Active=1;", ("$a", account.Value)),
                CultureInfo.InvariantCulture) == 0)
            {
                return Failure.NotFound;
            }
            long protectedWork = Convert.ToInt64(Scalar(connection, transaction, """
                SELECT (SELECT COUNT(*) FROM Drafts d WHERE d.AccountId=$a AND NOT EXISTS
                    (SELECT 1 FROM OutboxSubmissions o WHERE o.AccountId=d.AccountId
                    AND o.SourceDraftId=d.DraftId AND o.State<>$cancelled))
                    + (SELECT COUNT(*) FROM OutboxSubmissions WHERE AccountId=$a AND State NOT IN($done,$cancelled))
                    + (SELECT COUNT(*) FROM PendingOperations WHERE AccountId=$a AND State NOT IN($finished,$dismissed));
                """, ("$a", account.Value), ("$done", (int)SubmissionState.Completed),
                ("$cancelled", (int)SubmissionState.Cancelled), ("$finished", (int)PendingState.Completed),
                ("$dismissed", (int)PendingState.Dismissed)), CultureInfo.InvariantCulture);
            if (protectedWork > 0)
            {
                return Failure.NeedsReview;
            }
            Execute(connection, transaction, "UPDATE Accounts SET Active=0 WHERE AccountId=$a;", ("$a", account.Value));
            transaction.Commit();
            return Failure.None;
        }, cancellationToken);
    }

    /// <summary>Changes only the active account's local label.</summary>
    public Task<Failure> RenameAccountAsync(AccountId account, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 || name.Any(char.IsControl))
        {
            return Task.FromResult(Failure.Validation);
        }
        return WriteAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "UPDATE Accounts SET DisplayName=$name WHERE AccountId=$a AND Active=1;",
                ("$name", name.Trim()),
                ("$a", account.Value)
            );
            return command.ExecuteNonQuery() == 1 ? Failure.None : Failure.NotFound;
        }, cancellationToken);
    }

    public Task<long> CachedMessageCountAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection => Convert.ToInt64(
            Scalar(connection, null, "SELECT COUNT(*) FROM Messages m JOIN Accounts a ON a.AccountId=m.AccountId WHERE a.Active=1;"),
            CultureInfo.InvariantCulture
        ), cancellationToken);
    }

    public Task<FolderRoles?> ReadRolesAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Value FROM Preferences WHERE Scope=$scope AND Key='roles' AND PayloadVersion=1;",
                ("$scope", "account:" + account.Value.ToString(CultureInfo.InvariantCulture))
            );
            if (command.ExecuteScalar() is not string json)
            {
                return null;
            }

            FolderRoles? roles = JsonSerializer.Deserialize<FolderRoles>(json);
            return roles is not null && roles.IsValid ? roles : null;
        }, cancellationToken);
    }

    public Task SaveMailboxesAsync(
        AccountId account,
        ImmutableArray<MailboxSnapshot> mailboxes,
        FolderRoles? roles,
        CancellationToken cancellationToken
    )
    {
        if (mailboxes.Any(item => item.Account != account) || roles is not null && !roles.IsValid)
        {
            throw new ArgumentException("Invalid mailbox catalog or roles.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (MailboxSnapshot original in mailboxes)
            {
                MailboxSnapshot mailbox = roles is null ? original : roles.Apply(original);
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO Collections(AccountId,RemoteKey,Name,Role,Selectable,Writable,UidValidity) " +
                    "VALUES($a,$key,$name,$role,$selectable,$writable,$validity) " +
                    "ON CONFLICT(AccountId,RemoteKey) DO UPDATE SET Name=excluded.Name,Role=excluded.Role," +
                    "Selectable=excluded.Selectable,Writable=excluded.Writable;",
                    ("$a", account.Value),
                    ("$key", mailbox.RemoteKey),
                    ("$name", mailbox.Name),
                    ("$role", (int)mailbox.Role),
                    ("$selectable", mailbox.Selectable),
                    ("$writable", mailbox.Writable),
                    ("$validity", mailbox.UidValidity)
                );
            }

            if (roles is not null)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO Preferences(Scope,Key,Value,PayloadVersion) VALUES($scope,'roles',$value,1) " +
                    "ON CONFLICT(Scope,Key) DO UPDATE SET Value=excluded.Value,PayloadVersion=1;",
                    ("$scope", "account:" + account.Value.ToString(CultureInfo.InvariantCulture)),
                    ("$value", JsonSerializer.Serialize(roles))
                );
            }

            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<AppearanceSettings> ReadAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Value FROM Preferences WHERE Scope='client' AND Key='appearance' AND PayloadVersion=1;"
            );
            if (command.ExecuteScalar() is not string json)
            {
                return AppearanceSettings.Default;
            }

            try
            {
                AppearanceSettings? settings = JsonSerializer.Deserialize<AppearanceSettings>(json);
                return settings is not null && settings.IsValid ? settings : AppearanceSettings.Default;
            }
            catch (JsonException)
            {
                return AppearanceSettings.Default;
            }
        }, cancellationToken);
    }

    public async Task<Failure> SaveAsync(AppearanceSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.IsValid)
        {
            return Failure.Validation;
        }

        await WriteAsync(connection =>
        {
            Execute(
                connection,
                null,
                "INSERT INTO Preferences(Scope,Key,Value,PayloadVersion) VALUES('client','appearance',$value,1) " +
                "ON CONFLICT(Scope,Key) DO UPDATE SET Value=excluded.Value,PayloadVersion=1;",
                ("$value", JsonSerializer.Serialize(settings))
            );
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return Failure.None;
    }

    public Task<DesktopPreferences> ReadDesktopPreferencesAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Value FROM Preferences WHERE Scope='client' AND Key='desktop' AND PayloadVersion=1;"
            );
            if (command.ExecuteScalar() is not string json)
            {
                return DesktopPreferences.Default;
            }

            try
            {
                DesktopPreferences preferences = JsonSerializer.Deserialize<DesktopPreferences>(json) ?? DesktopPreferences.Default;
                return preferences with
                {
                    Updates = preferences.Updates ?? UpdatePreferences.Default
                };
            }
            catch (JsonException)
            {
                return DesktopPreferences.Default;
            }
        }, cancellationToken);
    }

    public Task SaveDesktopPreferencesAsync(DesktopPreferences preferences, CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            Execute(
                connection,
                null,
                "INSERT INTO Preferences(Scope,Key,Value,PayloadVersion) VALUES('client','desktop',$value,1) " +
                "ON CONFLICT(Scope,Key) DO UPDATE SET Value=excluded.Value,PayloadVersion=1;",
                ("$value", JsonSerializer.Serialize(preferences))
            );
            return true;
        }, cancellationToken);
    }

    public Task<ImmutableArray<AccountConfiguration>> AccountsAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT a.AccountId,a.DisplayName,a.Configuration,a.Capabilities,p.Value FROM Accounts a " +
                "LEFT JOIN Preferences p ON p.Scope='identity:'||a.AccountId AND p.Key='primary' AND p.PayloadVersion=1 " +
                "WHERE a.Active=1 ORDER BY a.AccountId;"
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<AccountConfiguration>.Builder accounts = ImmutableArray.CreateBuilder<AccountConfiguration>();
            while (reader.Read())
            {
                if (!reader.GetString(2).StartsWith("[", StringComparison.Ordinal))
                {
                    continue;
                }

                MailEndpoint[] endpoints = JsonSerializer.Deserialize<MailEndpoint[]>(reader.GetString(2))!;
                if (endpoints.Length == 2)
                {
                    accounts.Add(new AccountConfiguration(
                        new AccountId(reader.GetInt64(0)),
                        reader.GetString(1),
                        endpoints[0],
                        endpoints[1],
                        ReadCapabilities(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : ReadIdentity(reader.GetString(4))
                    ));
                }
            }

            return accounts.ToImmutable();
        }, cancellationToken);
    }

    public Task<ImmutableArray<string>> RecognisedRecipientsAsync(AccountId account, string prefix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 320)
        {
            return Task.FromResult(ImmutableArray<string>.Empty);
        }

        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                """
                SELECT Address FROM (
                    SELECT r.Address,o.Created AS LastUsed FROM SubmissionRecipients r
                    JOIN OutboxSubmissions o ON o.SubmissionId=r.SubmissionId
                    JOIN Accounts a ON a.AccountId=o.AccountId AND a.Active=1
                    WHERE o.AccountId=$a AND instr(lower(r.Address),lower($p))>0
                    UNION ALL
                    SELECT m.Participants,m.Date AS LastUsed FROM Messages m
                    JOIN Accounts a ON a.AccountId=m.AccountId AND a.Active=1
                    WHERE m.AccountId=$a AND length(m.Participants)<=2048
                        AND instr(lower(m.Participants),lower($p))>0
                ) GROUP BY Address COLLATE NOCASE ORDER BY MAX(LastUsed) DESC,Address LIMIT 100;
                """,
                ("$a", account.Value),
                ("$p", prefix.Trim())
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<string>.Builder result = ImmutableArray.CreateBuilder<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read() && result.Count < 20)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string candidate = reader.GetString(0);
                if (candidate.Contains('\r') || candidate.Contains('\n'))
                {
                    continue;
                }

                MailAddressCollection addresses = new MailAddressCollection();
                try
                {
                    addresses.Add(candidate);
                }
                catch (FormatException)
                {
                    continue;
                }

                foreach (MailAddress address in addresses)
                {
                    if (address.Address.Length <= 320 && address.Address.Contains(prefix.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(address.Address))
                    {
                        result.Add(address.Address);
                        if (result.Count == 20)
                        {
                            break;
                        }
                    }
                }
            }

            return result.ToImmutable();
        }, cancellationToken);
    }

    public Task<ImmutableArray<MailboxUnreadCount>> MailboxUnreadCountsAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                """
                SELECT c.FolderId,COUNT(m.MessageId)
                FROM Collections c
                JOIN Accounts active ON active.AccountId=c.AccountId AND active.Active=1
                LEFT JOIN Memberships x ON x.AccountId=c.AccountId AND x.FolderId=c.FolderId
                LEFT JOIN Messages m ON m.AccountId=x.AccountId AND m.MessageId=x.MessageId AND m.IsRead=0
                    AND (EXISTS(SELECT 1 FROM ImapInstances i INDEXED BY ImapMessage WHERE i.AccountId=x.AccountId
                        AND i.MessageId=x.MessageId AND i.FolderId=x.FolderId AND i.UidValidity=c.UidValidity)
                        OR NOT EXISTS(SELECT 1 FROM ImapInstances i INDEXED BY ImapMessage WHERE i.AccountId=x.AccountId
                            AND i.MessageId=x.MessageId AND i.FolderId=x.FolderId))
                WHERE c.AccountId=$a
                GROUP BY c.FolderId;
                """,
                ("$a", account.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<MailboxUnreadCount>.Builder counts = ImmutableArray.CreateBuilder<MailboxUnreadCount>();
            while (reader.Read())
            {
                counts.Add(new MailboxUnreadCount(account, new FolderId(reader.GetInt64(0)), reader.GetInt64(1)));
            }

            return counts.ToImmutable();
        }, cancellationToken);
    }

    public Task<ImmutableArray<MailboxSnapshot>> MailboxesAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT c.AccountId,c.FolderId,c.RemoteKey,c.Name,c.Role,c.Selectable,c.Writable,c.UidValidity FROM Collections c " +
                "JOIN Accounts a ON a.AccountId=c.AccountId WHERE a.Active=1 ORDER BY c.AccountId,c.Name;"
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<MailboxSnapshot>.Builder mailboxes = ImmutableArray.CreateBuilder<MailboxSnapshot>();
            while (reader.Read())
            {
                mailboxes.Add(new MailboxSnapshot(
                    new AccountId(reader.GetInt64(0)),
                    new FolderId(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    (MailboxRole)reader.GetInt32(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    checked((uint)reader.GetInt64(7))
                ));
            }

            return mailboxes.ToImmutable();
        }, cancellationToken);
    }

    public Task SaveCapabilitiesAsync(AccountId account, Capabilities capabilities, CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            Execute(
                connection,
                null,
                "UPDATE Accounts SET Capabilities=$c WHERE AccountId=$a;",
                ("$c", JsonSerializer.Serialize(capabilities)),
                ("$a", account.Value)
            );
            return true;
        }, cancellationToken);
    }

    public Task<Failure> SaveIdentityAsync(AccountId account, ComposeIdentity identity, CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            if (Convert.ToInt64(Scalar(
                connection,
                null,
                "SELECT count(*) FROM Accounts WHERE AccountId=$a AND Active=1;",
                ("$a", account.Value)
            ), CultureInfo.InvariantCulture) != 1)
            {
                return Failure.NotFound;
            }

            Execute(
                connection,
                null,
                "INSERT INTO Preferences(Scope,Key,Value,PayloadVersion) VALUES($s,'primary',$v,1) " +
                "ON CONFLICT(Scope,Key) DO UPDATE SET Value=excluded.Value,PayloadVersion=1;",
                ("$s", "identity:" + account.Value.ToString(CultureInfo.InvariantCulture)),
                ("$v", JsonSerializer.Serialize(identity))
            );
            return Failure.None;
        }, cancellationToken);
    }

    public Task<ImmutableArray<DraftSnapshot>> DraftsAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT DraftId FROM Drafts d WHERE AccountId=$a AND NOT EXISTS " +
                "(SELECT 1 FROM OutboxSubmissions o WHERE o.AccountId=d.AccountId AND o.SourceDraftId=d.DraftId AND o.State<>$cancelled) " +
                "ORDER BY d.rowid DESC LIMIT 200;",
                ("$a", account.Value),
                ("$cancelled", (int)SubmissionState.Cancelled)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<Guid>.Builder ids = ImmutableArray.CreateBuilder<Guid>();
            while (reader.Read())
            {
                ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
            }

            reader.Close();
            return ids.Select(id => ReadDraft(connection, null, id, account)!).ToImmutableArray();
        }, cancellationToken);
    }

    public Task<ImmutableArray<DraftVersionSnapshot>> DraftVersionsAsync(
        AccountId account,
        Guid draft,
        CancellationToken cancellationToken
    )
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT v.VersionKey,v.Origin,b.BlobId,b.Length,b.Hash,b.Protected FROM DraftVersions v " +
                "JOIN Blobs b USING(AccountId,BlobId) WHERE v.AccountId=$a AND v.DraftId=$d " +
                "ORDER BY v.Created DESC,v.VersionKey DESC LIMIT 200;",
                ("$a", account.Value),
                ("$d", draft.ToString("N"))
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<DraftVersionSnapshot>.Builder versions = ImmutableArray.CreateBuilder<DraftVersionSnapshot>();
            while (reader.Read())
            {
                versions.Add(new DraftVersionSnapshot(
                    draft,
                    account,
                    reader.GetString(0),
                    reader.GetString(1),
                    new BlobReference(
    Guid.ParseExact(reader.GetString(2), "N"),
    account,
    reader.GetInt64(3),
    reader.GetString(4),
    reader.GetBoolean(5)
)
                ));
            }

            return versions.ToImmutable();
        }, cancellationToken);
    }

    public Task<ImmutableArray<OutboxEntry>> OutboxAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Frozen,State,Revision,Evidence,SubmissionId,AccountId,BlobId FROM OutboxSubmissions " +
                "WHERE AccountId=$a ORDER BY CASE WHEN State IN($completed,$cancelled) THEN 1 ELSE 0 END,Created DESC LIMIT 200;",
                ("$a", account.Value),
                ("$completed", (int)SubmissionState.Completed),
                ("$cancelled", (int)SubmissionState.Cancelled)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<OutboxEntry>.Builder entries = ImmutableArray.CreateBuilder<OutboxEntry>();
            while (reader.Read())
            {
                entries.Add(ReadOutbox(reader));
            }

            return entries.ToImmutable();
        }, cancellationToken);
    }

    public static async Task<Outcome<SqliteProfile>> OpenAsync(
        string path,
        TimeProvider time,
        CancellationToken cancellationToken
    )
    {
        FileStream? lease = null;
        try
        {
            lease = await ProfileMigration.AdmitAsync(path, cancellationToken).ConfigureAwait(false);
            SqliteProfile profile = new SqliteProfile(path, time, lease);
            lease = null;
            return Outcome<SqliteProfile>.Success(profile);
        }
        catch (ProfileAdmissionException exception)
        {
            if (exception.Failure == Failure.Corrupt)
            {
                PreserveCorruption(path);
            }

            return Outcome<SqliteProfile>.Fail(exception.Failure);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            PreserveCorruption(path);
            return Outcome<SqliteProfile>.Fail(Failure.Corrupt);
        }
        catch (SqliteException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.Unavailable);
        }
        catch (InvalidDataException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.NeedsReview);
        }
        catch (JsonException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.NeedsReview);
        }
        catch (FormatException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.NeedsReview);
        }
        catch (IOException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return Outcome<SqliteProfile>.Fail(Failure.Unavailable);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public Task AddAccountAsync(AccountId account, string displayName, CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            Execute(
                connection,
                null,
                "INSERT INTO Accounts(AccountId,ProviderKind,DisplayName) VALUES($a,'Imap',$n) " +
                "ON CONFLICT(AccountId) DO UPDATE SET DisplayName=excluded.DisplayName;",
                ("$a", account.Value),
                ("$n", displayName)
            );
            return true;
        }, cancellationToken);
    }

    public Task<Checkpoint?> ReadCheckpointAsync(AccountId account, string scope, CancellationToken cancellationToken)
    {
        return ReadAsync<Checkpoint?>(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Generation,Cursor,ModSequence,Complete FROM SyncState WHERE AccountId=$a AND Scope=$s;",
                ("$a", account.Value),
                ("$s", scope)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new Checkpoint(
                account,
                scope,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetBoolean(3)
            );
        }, cancellationToken);
    }

    public Task CommitAsync(MailboxSnapshot mailbox, MetadataPage page, CancellationToken cancellationToken)
    {
        if (mailbox.Account != page.Checkpoint.Account || mailbox.RemoteKey != page.Checkpoint.Scope ||
            page.Messages.Any(message => message.Account != mailbox.Account || message.Provider.Account != mailbox.Account) ||
            page.Removed.Any(identity => identity.Account != mailbox.Account) || page.Messages.Length > 1000)
        {
            throw new ArgumentException("A reconciliation page must have one account and scope.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                INSERT INTO Collections(AccountId,RemoteKey,Name,Role,Selectable,Writable,UidValidity)
                VALUES($a,$s,$n,$r,$x,$w,$v)
                ON CONFLICT(AccountId,RemoteKey) DO UPDATE SET Name=excluded.Name,Role=excluded.Role,
                Selectable=excluded.Selectable,Writable=excluded.Writable,UidValidity=excluded.UidValidity;
                """,
                ("$a", mailbox.Account.Value),
                ("$s", mailbox.RemoteKey),
                ("$n", mailbox.Name),
                ("$r", (int)mailbox.Role),
                ("$x", mailbox.Selectable),
                ("$w", mailbox.Writable),
                ("$v", mailbox.UidValidity)
            );
            long folder = Convert.ToInt64(Scalar(
                connection,
                transaction,
                "SELECT FolderId FROM Collections WHERE AccountId=$a AND RemoteKey=$s;",
                ("$a", mailbox.Account.Value),
                ("$s", mailbox.RemoteKey)
            ), CultureInfo.InvariantCulture);
            foreach (MessageSnapshot message in page.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImapIdentity? imap = message.Provider.Imap;
                if (imap is not null && (imap.Account != mailbox.Account || imap.Mailbox != mailbox.RemoteKey ||
                    imap.UidValidity != mailbox.UidValidity || imap.Uid == 0))
                {
                    throw new ArgumentException("IMAP generation does not match the reconciliation scope.");
                }

                long id = Convert.ToInt64(
                    Scalar(
    connection,
    transaction,
    """
                    INSERT INTO Messages(AccountId,ProviderKey,MessageIdHeader,Subject,Participants,Date,Size,
                        IsRead,Flagged,Availability,NativeThreadId)
                    VALUES($a,$k,$h,$s,$p,$d,$z,$r,$f,$v,$t)
                    ON CONFLICT(AccountId,ProviderKey) DO UPDATE SET MessageIdHeader=excluded.MessageIdHeader,
                        Subject=excluded.Subject,Participants=excluded.Participants,Date=excluded.Date,Size=excluded.Size,
                        IsRead=CASE WHEN Messages.Pending IN (0,6,7) THEN excluded.IsRead ELSE Messages.IsRead END,
                        Flagged=CASE WHEN Messages.Pending IN (0,6,7) THEN excluded.Flagged ELSE Messages.Flagged END
                    RETURNING MessageId;
                    """,
    ("$a", message.Account.Value),
    ("$k", message.Provider.Key),
    ("$h", message.MessageIdHeader),
    ("$s", message.Subject),
    ("$p", message.Participants),
    ("$d", message.Date.ToUnixTimeMilliseconds()),
    ("$z", message.Size),
    ("$r", message.Read),
    ("$f", message.Flagged),
    ("$v", (int)ContentAvailability.Missing),
    ("$t", message.NativeThreadId)
),
                    CultureInfo.InvariantCulture
                );
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO Memberships VALUES($a,$m,$f,$g)
                    ON CONFLICT(AccountId,MessageId,FolderId) DO UPDATE SET SeenGeneration=excluded.SeenGeneration;
                    """,
                    ("$a", mailbox.Account.Value),
                    ("$m", id),
                    ("$f", folder),
                    ("$g", page.Checkpoint.Generation)
                );
                if (imap is not null)
                {
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO ImapInstances VALUES($a,$f,$v,$u,$m,$s) " +
                        "ON CONFLICT(AccountId,FolderId,UidValidity,Uid) DO UPDATE SET ModSeq=excluded.ModSeq;",
                        ("$a", mailbox.Account.Value),
                        ("$f", folder),
                        ("$v", imap.UidValidity),
                        ("$u", imap.Uid),
                        ("$m", id),
                        ("$s", page.Checkpoint.ModSequence.ToString(CultureInfo.InvariantCulture))
                    );
                }

                Reindex(connection, transaction, id);
            }

            foreach (ProviderIdentity removed in page.Removed)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    DELETE FROM Memberships WHERE AccountId=$a AND FolderId=$f AND MessageId IN
                    (SELECT MessageId FROM Messages WHERE AccountId=$a AND ProviderKey=$k);
                    """,
                    ("$a", mailbox.Account.Value),
                    ("$f", folder),
                    ("$k", removed.Key)
                );
            }

            if (!page.Seen.IsDefault)
            {
                foreach (ProviderIdentity seen in page.Seen)
                {
                    if (seen.Account != mailbox.Account)
                    {
                        throw new ArgumentException("Seen membership belongs to another account.");
                    }

                    Execute(
                        connection,
                        transaction,
                        """
                        UPDATE Memberships SET SeenGeneration=$g WHERE AccountId=$a AND FolderId=$f AND MessageId IN
                        (SELECT MessageId FROM Messages WHERE AccountId=$a AND ProviderKey=$k);
                        """,
                        ("$g", page.Checkpoint.Generation),
                        ("$a", mailbox.Account.Value),
                        ("$f", folder),
                        ("$k", seen.Key)
                    );
                }
            }

            if (page.FullEnumeration && page.Checkpoint.Complete)
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM Memberships WHERE AccountId=$a AND FolderId=$f AND SeenGeneration<>$g;",
                    ("$a", mailbox.Account.Value),
                    ("$f", folder),
                    ("$g", page.Checkpoint.Generation)
                );
            }

            Execute(
                connection,
                transaction,
                """
                INSERT INTO SyncState VALUES($a,$s,$g,$c,$m,$x,$t)
                ON CONFLICT(AccountId,Scope) DO UPDATE SET Generation=excluded.Generation,Cursor=excluded.Cursor,
                    ModSequence=excluded.ModSequence,Complete=excluded.Complete,LastSuccess=excluded.LastSuccess;
                """,
                ("$a", mailbox.Account.Value),
                ("$s", mailbox.RemoteKey),
                ("$g", page.Checkpoint.Generation),
                ("$c", page.Checkpoint.Cursor),
                ("$m", page.Checkpoint.ModSequence.ToString(CultureInfo.InvariantCulture)),
                ("$x", page.Checkpoint.Complete),
                ("$t", Now())
            );
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task StoreBodyAsync(
        AccountId account,
        MessageId message,
        BlobReference blob,
        string text,
        CancellationToken cancellationToken
    )
    {
        return StoreBodyAsync(
            account,
            message,
            blob,
            new MimeSnapshot("", "", null, text, null, []),
            cancellationToken
        );
    }

    public Task CommitAccountAsync(CanonicalPage page, CancellationToken cancellationToken)
    {
        AccountId account = page.Checkpoint.Account;
        if (page.Checkpoint.Scope != "$account" || page.Messages.Length > 1000 ||
            page.Messages.Any(message => message.Account != account || message.Provider.Account != account ||
                message.Provider.Imap is not null) || page.Collections.Any(folder => folder.Account != account))
        {
            throw new ArgumentException("Canonical pages require one account and an account-level checkpoint.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (MailboxSnapshot folder in page.Collections)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO Collections(AccountId,RemoteKey,Name,Role,Selectable,Writable,UidValidity,Kind)
                    VALUES($a,$k,$n,$r,$s,$w,0,'Label')
                    ON CONFLICT(AccountId,RemoteKey) DO UPDATE SET Name=excluded.Name,Role=excluded.Role;
                    """,
                    ("$a", account.Value),
                    ("$k", folder.RemoteKey),
                    ("$n", folder.Name),
                    ("$r", (int)folder.Role),
                    ("$s", folder.Selectable),
                    ("$w", folder.Writable)
                );
            }

            foreach (MessageSnapshot message in page.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id = Convert.ToInt64(
                    Scalar(
    connection,
    transaction,
    """
                    INSERT INTO Messages(AccountId,ProviderKey,MessageIdHeader,Subject,Participants,Date,Size,
                        IsRead,Flagged,Availability,NativeThreadId)
                    VALUES($a,$k,$h,$s,$p,$d,$z,$r,$f,0,$t)
                    ON CONFLICT(AccountId,ProviderKey) DO UPDATE SET Subject=excluded.Subject,
                        Participants=excluded.Participants,Date=excluded.Date,Size=excluded.Size,
                        IsRead=CASE WHEN Messages.Pending IN(0,6,7) THEN excluded.IsRead ELSE Messages.IsRead END,
                        Flagged=CASE WHEN Messages.Pending IN(0,6,7) THEN excluded.Flagged ELSE Messages.Flagged END
                    RETURNING MessageId;
                    """,
    ("$a", account.Value),
    ("$k", message.Provider.Key),
    ("$h", message.MessageIdHeader),
    ("$s", message.Subject),
    ("$p", message.Participants),
    ("$d", message.Date.ToUnixTimeMilliseconds()),
    ("$z", message.Size),
    ("$r", message.Read),
    ("$f", message.Flagged),
    ("$t", message.NativeThreadId)
),
                    CultureInfo.InvariantCulture
                );
                Reindex(connection, transaction, id);
            }

            foreach (MembershipChange membership in page.Memberships)
            {
                if (membership.Present)
                {
                    int changed = Execute(
                        connection,
                        transaction,
                        """
                        INSERT INTO Memberships
                        SELECT $a,m.MessageId,c.FolderId,$g FROM Messages m JOIN Collections c ON c.AccountId=m.AccountId
                        WHERE m.AccountId=$a AND m.ProviderKey=$m AND c.RemoteKey=$c
                        ON CONFLICT(AccountId,MessageId,FolderId) DO UPDATE SET SeenGeneration=excluded.SeenGeneration;
                        """,
                        ("$a", account.Value),
                        ("$m", membership.ProviderMessageKey),
                        ("$c", membership.CollectionKey),
                        ("$g", page.Checkpoint.Generation)
                    );
                    if (changed != 1)
                    {
                        throw new InvalidDataException("Membership refers to missing canonical metadata.");
                    }
                }
                else
                {
                    Execute(
                        connection,
                        transaction,
                        """
                        DELETE FROM Memberships WHERE AccountId=$a AND MessageId IN
                        (SELECT MessageId FROM Messages WHERE AccountId=$a AND ProviderKey=$m) AND FolderId IN
                        (SELECT FolderId FROM Collections WHERE AccountId=$a AND RemoteKey=$c);
                        """,
                        ("$a", account.Value),
                        ("$m", membership.ProviderMessageKey),
                        ("$c", membership.CollectionKey)
                    );
                }
            }

            foreach (string removed in page.RemovedMessages)
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM Memberships WHERE AccountId=$a AND MessageId IN " +
                    "(SELECT MessageId FROM Messages WHERE AccountId=$a AND ProviderKey=$k);",
                    ("$a", account.Value),
                    ("$k", removed)
                );
            }

            if (page.FullEnumeration && page.Checkpoint.Complete)
            {
                Execute(
                    connection,
                    transaction,
                    "DELETE FROM Memberships WHERE AccountId=$a AND SeenGeneration<>$g;",
                    ("$a", account.Value),
                    ("$g", page.Checkpoint.Generation)
                );
            }

            Execute(
                connection,
                transaction,
                """
                INSERT INTO SyncState VALUES($a,'$account',$g,$c,'0',$x,$t)
                ON CONFLICT(AccountId,Scope) DO UPDATE SET Generation=excluded.Generation,Cursor=excluded.Cursor,
                    Complete=excluded.Complete,LastSuccess=excluded.LastSuccess;
                """,
                ("$a", account.Value),
                ("$g", page.Checkpoint.Generation),
                ("$c", page.Checkpoint.Cursor),
                ("$x", page.Checkpoint.Complete),
                ("$t", Now())
            );
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task StoreBodyAsync(
        AccountId account,
        MessageId message,
        BlobReference blob,
        MimeSnapshot content,
        CancellationToken cancellationToken
    )
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            RegisterBlob(connection, transaction, account, blob);
            Execute(
                connection,
                transaction,
                "INSERT INTO Bodies(AccountId,MessageId,BlobId,Text,LastAccess) VALUES($a,$m,$b,$t,$d) " +
                "ON CONFLICT(AccountId,MessageId) DO UPDATE SET BlobId=excluded.BlobId,Text=excluded.Text,LastAccess=excluded.LastAccess;",
                ("$a", account.Value),
                ("$m", message.Value),
                ("$b", blob.Id.ToString("N")),
                ("$t", content.PlainText ?? ""),
                ("$d", Now())
            );
            foreach (AttachmentSnapshot attachment in content.Attachments)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO Attachments(AccountId,MessageId,PartKey,FileName,MediaType,Size)
                    VALUES($a,$m,$p,$n,$t,$s) ON CONFLICT(AccountId,MessageId,PartKey)
                    DO UPDATE SET FileName=excluded.FileName,MediaType=excluded.MediaType,Size=excluded.Size;
                    """,
                    ("$a", account.Value),
                    ("$m", message.Value),
                    ("$p", attachment.Part),
                    ("$n", attachment.Name),
                    ("$t", attachment.MediaType),
                    ("$s", attachment.Length)
                );
            }
            Execute(
                connection,
                transaction,
                "UPDATE Messages SET Availability=$v WHERE AccountId=$a AND MessageId=$m;",
                ("$v", (int)ContentAvailability.Available),
                ("$a", account.Value),
                ("$m", message.Value)
            );
            Reindex(connection, transaction, message.Value);
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<ImmutableArray<AttachmentSnapshot>> ReadAttachmentsAsync(
        AccountId account,
        MessageId message,
        CancellationToken cancellationToken
    )
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT PartKey,FileName,MediaType,Size FROM Attachments WHERE AccountId=$a AND MessageId=$m " +
                "AND substr(PartKey,1,5)='imap:' ORDER BY AttachmentId LIMIT 200;",
                ("$a", account.Value),
                ("$m", message.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<AttachmentSnapshot>.Builder attachments = ImmutableArray.CreateBuilder<AttachmentSnapshot>();
            while (reader.Read())
            {
                attachments.Add(new AttachmentSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    ContentAvailability.Missing
                ));
            }

            return attachments.ToImmutable();
        }, cancellationToken);
    }

    public Task<Failure> StoreAttachmentsAsync(
        MessageSnapshot message,
        ImmutableArray<AttachmentSnapshot> attachments,
        CancellationToken cancellationToken
    )
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!TargetMatches(connection, transaction, message.Account, message.Id, message.Provider))
            {
                return Failure.NeedsReview;
            }

            foreach (AttachmentSnapshot attachment in attachments)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO Attachments(AccountId,MessageId,PartKey,FileName,MediaType,Size) VALUES($a,$m,$p,$n,$t,$s) " +
                    "ON CONFLICT(AccountId,MessageId,PartKey) DO UPDATE SET FileName=excluded.FileName," +
                    "MediaType=excluded.MediaType,Size=excluded.Size;",
                    ("$a", message.Account.Value),
                    ("$m", message.Id.Value),
                    ("$p", attachment.Part),
                    ("$n", attachment.Name),
                    ("$t", attachment.MediaType),
                    ("$s", attachment.Length)
                );
            }

            transaction.Commit();
            return Failure.None;
        }, cancellationToken);
    }

    public Task<long> EvictAsync(AccountId account, long budgetBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            long total = Convert.ToInt64(Scalar(
                connection,
                transaction,
                "SELECT COALESCE(sum(Length),0) FROM Blobs WHERE AccountId=$a AND Protected=0 " +
                "AND BlobId IN(SELECT BlobId FROM Bodies WHERE AccountId=$a);",
                ("$a", account.Value)
            ), CultureInfo.InvariantCulture);
            List<(long Message, string Blob, long Length)> candidates = new List<(long, string, long)>();
            using (SqliteCommand command = Command(
                connection,
                transaction,
                """
                SELECT Bodies.MessageId,Blobs.BlobId,Blobs.Length FROM Bodies JOIN Blobs USING(AccountId,BlobId)
                WHERE Bodies.AccountId=$a AND Blobs.Protected=0 ORDER BY Bodies.LastAccess,Bodies.MessageId;
                """,
                ("$a", account.Value)
            ))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    candidates.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            foreach ((long message, string blob, long length) in candidates)
            {
                if (total <= budgetBytes)
                {
                    break;
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE Bodies SET BlobId=NULL,Text='' WHERE AccountId=$a AND MessageId=$m;",
                    ("$a", account.Value),
                    ("$m", message)
                );
                Execute(
                    connection,
                    transaction,
                    "UPDATE Messages SET Availability=$v WHERE AccountId=$a AND MessageId=$m;",
                    ("$v", (int)ContentAvailability.Evicted),
                    ("$a", account.Value),
                    ("$m", message)
                );
                Reindex(connection, transaction, message);
                long remainingReferences = Convert.ToInt64(Scalar(
                    connection,
                    transaction,
                    "SELECT count(*) FROM Bodies WHERE AccountId=$a AND BlobId=$b;",
                    ("$a", account.Value),
                    ("$b", blob)
                ), CultureInfo.InvariantCulture);
                if (remainingReferences == 0)
                {
                    total -= length;
                }
            }

            transaction.Commit();
            // Objects are retained as orphan candidates until a separately authorised retention cleanup.
            return total;
        }, cancellationToken);
    }

    public Task<int> CleanupDisposableOrphansAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        if (olderThan > _time.GetUtcNow() - TimeSpan.FromHours(1))
        {
            throw new ArgumentException("Orphan retention must protect recent promotions.");
        }

        return WriteAsync(connection =>
        {
            // The writer barrier prevents reference creation between proof of absence and object cleanup.
            HashSet<Guid> referenced = new HashSet<Guid>();
            using SqliteCommand command = Command(
                connection,
                null,
                """
                SELECT BlobId FROM Bodies WHERE BlobId IS NOT NULL
                UNION SELECT BlobId FROM Attachments WHERE BlobId IS NOT NULL
                UNION SELECT BlobId FROM Drafts
                UNION SELECT BlobId FROM DraftVersions
                UNION SELECT BlobId FROM OutboxSubmissions;
                """
            );
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                referenced.Add(Guid.ParseExact(reader.GetString(0), "N"));
            }

            reader.Close();
            return _blobs.CleanupDisposable(referenced, olderThan);
        }, cancellationToken);
    }

    public Task<Outcome<SearchPage>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        if (request.Limit is < 1 or > 200 || request.Terms.Length > 1024 ||
            (!request.Unified && request.Account is null) || (request.Unified && request.Account is not null) ||
            (request.Folder is not null && request.Account is null))
        {
            return Task.FromResult(Outcome<SearchPage>.Fail(Failure.Validation));
        }

        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            request with
            {
                Continuation = null
            }
        ))));
        long date = long.MaxValue;
        long id = long.MaxValue;
        if (request.Continuation is not null)
        {
            try
            {
                string[] fields = Encoding.UTF8.GetString(Convert.FromBase64String(request.Continuation)).Split('|');
                if (fields.Length != 3 || fields[0] != scope)
                {
                    return Task.FromResult(Outcome<SearchPage>.Fail(Failure.Validation));
                }

                date = long.Parse(fields[1], CultureInfo.InvariantCulture);
                id = long.Parse(fields[2], CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return Task.FromResult(Outcome<SearchPage>.Fail(Failure.Validation));
            }
        }

        string literal = string.Join(" AND ", Regex.Matches(request.Terms, @"[\p{L}\p{N}\p{M}]+")
            .Select(match => "\"" + match.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        return ReadAsync(connection =>
        {
            string dateIndex = request.Unified ? "UnifiedMessageDate" : "MessageDate";
            string accountFilter = request.Unified ? "1=1" : "m.AccountId=$a";
            string sql = $$"""
                SELECT m.MessageId,m.Date,c.RemoteKey,i.UidValidity,i.Uid
                FROM Messages m INDEXED BY {{dateIndex}}
                JOIN Accounts active ON active.AccountId=m.AccountId AND active.Active=1
                LEFT JOIN ImapInstances i ON i.AccountId=m.AccountId AND i.MessageId=m.MessageId
                LEFT JOIN Collections c ON c.AccountId=i.AccountId AND c.FolderId=i.FolderId
                WHERE {{accountFilter}} AND ($u=0 OR m.IsRead=0)
                AND (i.UidValidity IS NULL OR i.UidValidity=c.UidValidity)
                AND EXISTS(SELECT 1 FROM Memberships x WHERE x.AccountId=m.AccountId AND x.MessageId=m.MessageId
                    AND ($f IS NULL OR x.FolderId=$f)
                    AND ($inbox=0 OR EXISTS(SELECT 1 FROM Collections inbox WHERE inbox.AccountId=x.AccountId
                        AND inbox.FolderId=x.FolderId AND inbox.Role=$role)))
                AND (m.Date<$d OR (m.Date=$d AND m.MessageId<$i))
                """;
            if (literal.Length > 0)
            {
                sql += " AND m.MessageId IN(SELECT rowid FROM SearchText WHERE SearchText MATCH $q)";
            }

            sql += " ORDER BY m.Date DESC,m.MessageId DESC LIMIT $n";
            // Materialise previews only for the bounded page, after matching and ordering metadata.
            // Otherwise common FTS terms read body/attachment evidence for every match before LIMIT.
            sql = """
                SELECT m.*,page.RemoteKey,page.UidValidity,page.Uid,
                    (SELECT substr(s.Body,1,200) FROM SearchText s WHERE s.rowid=m.MessageId) AS CachedPreview,
                    EXISTS(SELECT 1 FROM Attachments a WHERE a.AccountId=m.AccountId AND a.MessageId=m.MessageId)
                FROM (
                """ + sql + """
                ) page JOIN Messages m ON m.MessageId=page.MessageId
                ORDER BY m.Date DESC,m.MessageId DESC;
                """;
            using SqliteCommand command = Command(
                connection,
                null,
                sql,
                ("$a", request.Account?.Value),
                ("$u", request.UnreadOnly),
                ("$f", request.Folder?.Value),
                ("$d", date),
                ("$i", id),
                ("$q", literal),
                ("$n", request.Limit + 1),
                ("$inbox", request.InboxOnly),
                ("$role", (int)MailboxRole.Inbox)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<MessageSnapshot>.Builder messages = ImmutableArray.CreateBuilder<MessageSnapshot>();
            while (reader.Read())
            {
                messages.Add(ReadMessage(reader));
            }

            string? continuation = null;
            if (messages.Count > request.Limit)
            {
                messages.RemoveAt(messages.Count - 1);
                MessageSnapshot last = messages[^1];
                continuation = Convert.ToBase64String(Encoding.UTF8.GetBytes(scope + "|" +
                    last.Date.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "|" +
                    last.Id.Value.ToString(CultureInfo.InvariantCulture)));
            }

            return Outcome<SearchPage>.Success(new SearchPage(messages.ToImmutable(), continuation));
        }, cancellationToken);
    }

    public Task<Outcome<BlobReference>> ReadBodyAsync(
        AccountId account,
        MessageId message,
        CancellationToken cancellationToken
    )
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT b.BlobId,b.Length,b.Hash,b.Protected FROM Bodies c JOIN Blobs b USING(AccountId,BlobId) " +
                "WHERE c.AccountId=$a AND c.MessageId=$m;",
                ("$a", account.Value),
                ("$m", message.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? Outcome<BlobReference>.Success(new BlobReference(
                Guid.ParseExact(reader.GetString(0), "N"),
                account,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetBoolean(3)
            )) : Outcome<BlobReference>.Fail(Failure.NotFound);
        }, cancellationToken);
    }

    public Task ConfigureAccountAsync(
        AccountId account,
        MailEndpoint incoming,
        MailEndpoint outgoing,
        CancellationToken cancellationToken
    )
    {
        return WriteAsync(connection => Execute(
            connection,
            null,
            "UPDATE Accounts SET Configuration=$c,CredentialReference=$r WHERE AccountId=$a;",
            ("$c", JsonSerializer.Serialize(new[] { incoming, outgoing })),
            ("$r", incoming.CredentialKey),
            ("$a", account.Value)
        ), cancellationToken);
    }

    public Task<bool> QueueAsync(MutationIntent intent, CancellationToken cancellationToken)
    {
        if (intent.PayloadVersion != 1 || intent.Id.Value == Guid.Empty || intent.Provider.Account != intent.Account)
        {
            throw new ArgumentException("Invalid mutation identity or payload version.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!TargetMatches(connection, transaction, intent))
            {
                return false;
            }

            int added = Execute(
                connection,
                transaction,
                "INSERT INTO PendingOperations VALUES($o,$a,$m,1,$p,$s,0,0,$d,0) ON CONFLICT(OperationId) DO NOTHING;",
                ("$o", intent.Id.Value.ToString("N")),
                ("$a", intent.Account.Value),
                ("$m", intent.Target.Value),
                ("$p", JsonSerializer.Serialize(intent)),
                ("$s", (int)PendingState.Queued),
                ("$d", Now())
            );
            if (added == 0)
            {
                return false;
            }

            string projection = intent.Kind switch
            {
                MutationKind.SetRead => ",IsRead=$v",
                MutationKind.SetFlagged => ",Flagged=$v",
                _ => ""
            };
            Execute(
                connection,
                transaction,
                "UPDATE Messages SET Pending=$s" + projection + " WHERE AccountId=$a AND MessageId=$m;",
                ("$s", (int)PendingState.Queued),
                ("$v", intent.Value),
                ("$a", intent.Account.Value),
                ("$m", intent.Target.Value)
            );
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<OperationEntry?> ReadNextAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync<OperationEntry?>(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Payload,State,Revision,Attempts,Due,Evidence,OperationId,AccountId,MessageId,PayloadVersion " +
                "FROM PendingOperations WHERE AccountId=$a AND State IN($q,$r,$i,$auth,$review) ORDER BY rowid LIMIT 1;",
                ("$a", account.Value),
                ("$q", (int)PendingState.Queued),
                ("$r", (int)PendingState.RetryScheduled),
                ("$i", (int)PendingState.InFlight),
                ("$auth", (int)PendingState.NeedsAuthentication),
                ("$review", (int)PendingState.NeedsReview)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadOperation(reader) : null;
        }, cancellationToken);
    }

    public Task<OperationEntry?> ClaimAsync(AccountId account, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return WriteAsync<OperationEntry?>(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            OperationEntry? operation;
            using (SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT Payload,State,Revision,Attempts,Due,Evidence,OperationId,AccountId,MessageId,PayloadVersion " +
                "FROM PendingOperations WHERE AccountId=$a AND State IN($q,$r,$i,$auth,$review) ORDER BY rowid LIMIT 1;",
                ("$a", account.Value),
                ("$q", (int)PendingState.Queued),
                ("$r", (int)PendingState.RetryScheduled),
                ("$i", (int)PendingState.InFlight),
                ("$auth", (int)PendingState.NeedsAuthentication),
                ("$review", (int)PendingState.NeedsReview)
            ))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                operation = reader.Read() ? ReadOperation(reader) : null;
            }

            if (operation is null || operation.State is not (PendingState.Queued or PendingState.RetryScheduled) ||
                operation.Due > now)
            {
                return null;
            }

            PendingState state = TargetMatches(connection, transaction, operation.Intent) ?
                PendingState.InFlight : PendingState.NeedsReview;
            Execute(
                connection,
                transaction,
                "UPDATE PendingOperations SET State=$s,Revision=Revision+1,Attempts=Attempts+1 WHERE OperationId=$o;",
                ("$s", (int)state),
                ("$o", operation.Intent.Id.Value.ToString("N"))
            );
            transaction.Commit();
            return state == PendingState.InFlight ?
                operation with
                {
                    State = state,
                    Revision = operation.Revision + 1,
                    Attempts = operation.Attempts + 1
                } : null;
        }, cancellationToken);
    }

    public Task<bool> FinishAsync(
        OperationEntry claim,
        PendingState state,
        Failure evidence,
        DateTimeOffset due,
        CancellationToken cancellationToken
    )
    {
        if (state is not (PendingState.Completed or PendingState.RetryScheduled or PendingState.NeedsReview or
            PendingState.NeedsAuthentication or PendingState.Failed))
        {
            throw new ArgumentException("Invalid operation result.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int changed = Execute(
                connection,
                transaction,
                "UPDATE PendingOperations SET State=$s,Evidence=$e,Due=$d,Revision=Revision+1 " +
                "WHERE OperationId=$o AND AccountId=$a AND State=$c AND Revision=$r;",
                ("$s", (int)state),
                ("$e", (int)evidence),
                ("$d", due.ToUnixTimeMilliseconds()),
                ("$o", claim.Intent.Id.Value.ToString("N")),
                ("$a", claim.Intent.Account.Value),
                ("$c", (int)PendingState.InFlight),
                ("$r", claim.Revision)
            );
            if (changed != 0)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    UPDATE Messages SET Pending=COALESCE(
                        (SELECT State FROM PendingOperations WHERE AccountId=$a AND MessageId=$m
                        AND State IN(1,2,3,4,5) ORDER BY rowid DESC LIMIT 1),$s)
                    WHERE AccountId=$a AND MessageId=$m;
                    """,
                    ("$s", (int)state),
                    ("$a", claim.Intent.Account.Value),
                    ("$m", claim.Intent.Target.Value)
                );
            }

            transaction.Commit();
            return changed == 1;
        }, cancellationToken);
    }

    public Task<DraftSnapshot> SaveAsync(
        Guid id,
        AccountId account,
        BlobReference content,
        CancellationToken cancellationToken
    )
    {
        if (!content.Protected)
        {
            throw new ArgumentException("Draft content must be protected.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            RegisterBlob(connection, transaction, account, content);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO Drafts VALUES($i,$a,NULL,1,0,0,$b,NULL,NULL,0,0)
                ON CONFLICT(DraftId) DO UPDATE SET Revision=Revision+1,BlobId=excluded.BlobId
                WHERE Drafts.AccountId=excluded.AccountId;
                """,
                ("$i", id.ToString("N")),
                ("$a", account.Value),
                ("$b", content.Id.ToString("N"))
            );
            DraftSnapshot draft = ReadDraft(connection, transaction, id, account) ??
                throw new ArgumentException("Draft identity belongs to another account.");
            Execute(
                connection,
                transaction,
                "INSERT INTO DraftVersions VALUES($i,$a,$v,$b,'Local',$t);",
                ("$i", id.ToString("N")),
                ("$a", account.Value),
                ("$v", "local:" + draft.Revision.ToString(CultureInfo.InvariantCulture)),
                ("$b", content.Id.ToString("N")),
                ("$t", Now())
            );
            transaction.Commit();
            return draft;
        }, cancellationToken);
    }

    public Task<DraftSnapshot?> ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection => ReadDraft(connection, null, id, account), cancellationToken);
    }

    public Task<bool> AcknowledgeAsync(
        DraftSnapshot transmitted,
        RemoteDraftResult result,
        CancellationToken cancellationToken
    )
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (result.ConflictingContent is not null)
            {
                if (!result.ConflictingContent.Protected)
                {
                    throw new ArgumentException("Conflicting draft content must be protected.");
                }

                RegisterBlob(connection, transaction, transmitted.Account, result.ConflictingContent);
                Execute(
                    connection,
                    transaction,
                    "INSERT OR IGNORE INTO DraftVersions VALUES($i,$a,$v,$b,'Remote',$t);",
                    ("$i", transmitted.Id.ToString("N")),
                    ("$a", transmitted.Account.Value),
                    ("$v", "remote:" + result.ConflictingContent.Id.ToString("N")),
                    ("$b", result.ConflictingContent.Id.ToString("N")),
                    ("$t", Now())
                );
            }

            if (result.Identity is not null && result.Identity.Account != transmitted.Account)
            {
                throw new ArgumentException("Remote draft account mismatch.");
            }

            int changed = Execute(
                connection,
                transaction,
                """
                UPDATE Drafts SET
                    PushedRevision=CASE WHEN $ok=1 THEN $r ELSE PushedRevision END,
                    BaseRevision=CASE WHEN $ok=1 THEN $r ELSE BaseRevision END,
                    RemoteIdentity=COALESCE($remote,RemoteIdentity),RemoteVersion=COALESCE($version,RemoteVersion),
                    Conflict=MAX(Conflict,$conflict),Held=CASE WHEN $ok=1 THEN 0 ELSE MAX(Held,$held) END
                WHERE DraftId=$i AND AccountId=$a AND Revision>=$r AND PushedRevision<$r;
                """,
                ("$ok", result.Failure == Failure.None && !result.Uncertain && result.ConflictingContent is null),
                ("$r", transmitted.Revision),
                ("$remote", result.Identity is null ? null : JsonSerializer.Serialize(result.Identity)),
                ("$version", result.Version),
                ("$conflict", result.ConflictingContent is not null),
                ("$held", result.Uncertain),
                ("$i", transmitted.Id.ToString("N")),
                ("$a", transmitted.Account.Value)
            );
            transaction.Commit();
            return changed == 1;
        }, cancellationToken);
    }

    public Task<bool> QueueAsync(FrozenSubmission submission, CancellationToken cancellationToken)
    {
        if (!submission.Mime.Protected || submission.Recipients.IsDefaultOrEmpty ||
            submission.Recipients.Distinct(StringComparer.OrdinalIgnoreCase).Count() != submission.Recipients.Length)
        {
            throw new ArgumentException("Frozen submission requires protected MIME and distinct recipients.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (submission.SourceDraft is Guid sourceDraft && Convert.ToInt64(Scalar(
                connection,
                transaction,
                "SELECT COUNT(*) FROM OutboxSubmissions WHERE AccountId=$a AND SourceDraftId=$draft AND State<>$cancelled;",
                ("$a", submission.Account.Value),
                ("$draft", sourceDraft.ToString("N")),
                ("$cancelled", (int)SubmissionState.Cancelled)
            ), CultureInfo.InvariantCulture) > 0)
            {
                return false;
            }
            RegisterBlob(connection, transaction, submission.Account, submission.Mime);
            Execute(
                connection,
                transaction,
                "INSERT INTO OutboxSubmissions VALUES($i,$a,$draft,$b,$f,$s,0,NULL,$t,$p);",
                ("$i", submission.Id.ToString("N")),
                ("$a", submission.Account.Value),
                ("$b", submission.Mime.Id.ToString("N")),
                ("$f", JsonSerializer.Serialize(submission)),
                ("$s", (int)SubmissionState.Queued),
                ("$t", Now()),
                ("$p", submission.Predecessor?.ToString("N")),
                ("$draft", submission.SourceDraft?.ToString("N"))
            );
            for (int index = 0; index < submission.Recipients.Length; index++)
            {
                Execute(
                    connection,
                    transaction,
                    "INSERT INTO SubmissionRecipients VALUES($i,$n,$a,$s,NULL);",
                    ("$i", submission.Id.ToString("N")),
                    ("$n", index),
                    ("$a", submission.Recipients[index]),
                    ("$s", (int)RecipientState.NotAccepted)
                );
            }

            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<bool> CancelQueuedAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken)
    {
        return WriteAsync(
            connection => Execute(
    connection,
    null,
    "UPDATE OutboxSubmissions SET State=$cancelled,Revision=Revision+1 " +
            "WHERE SubmissionId=$id AND AccountId=$account AND Revision=$revision AND State=$queued;",
    ("$cancelled", (int)SubmissionState.Cancelled),
    ("$id", id.ToString("N")),
    ("$account", account.Value),
    ("$revision", revision),
    ("$queued", (int)SubmissionState.Queued)
) == 1,
            cancellationToken
        );
    }

    public Task<bool> PrepareSentCopyRetryAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken)
    {
        return WriteAsync(connection => Execute(
            connection,
            null,
            "UPDATE OutboxSubmissions SET State=$accepted,Revision=Revision+1 " +
            "WHERE SubmissionId=$id AND AccountId=$account AND Revision=$revision AND State=$pending;",
            ("$accepted", (int)SubmissionState.Accepted),
            ("$id", id.ToString("N")),
            ("$account", account.Value),
            ("$revision", revision),
            ("$pending", (int)SubmissionState.SentCopyPending)
        ) == 1, cancellationToken);
    }

    public Task<OutboxEntry?> ClaimAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ClaimSubmissionAsync(null, account, cancellationToken);
    }

    public Task<OutboxEntry?> ClaimAsync(Guid id, AccountId account, CancellationToken cancellationToken)
    {
        return ClaimSubmissionAsync(id, account, cancellationToken);
    }

    public Task<bool> FinishAsync(OutboxEntry claim, SubmissionOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.State is not (SubmissionState.Accepted or SubmissionState.Rejected or
            SubmissionState.PartiallyAccepted or SubmissionState.Uncertain) ||
            !outcome.Recipients.Select(item => item.Address).SequenceEqual(claim.Submission.Recipients) ||
            (outcome.State == SubmissionState.Accepted && outcome.Recipients.Any(item => item.State != RecipientState.Accepted)))
        {
            throw new ArgumentException("Invalid frozen-recipient evidence.");
        }

        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int changed = Execute(
                connection,
                transaction,
                "UPDATE OutboxSubmissions SET State=$s,Evidence=$e,Revision=Revision+1 " +
                "WHERE SubmissionId=$i AND AccountId=$a AND State=$c AND Revision=$r;",
                ("$s", (int)outcome.State),
                ("$e", JsonSerializer.Serialize(outcome)),
                ("$i", claim.Submission.Id.ToString("N")),
                ("$a", claim.Submission.Account.Value),
                ("$c", (int)SubmissionState.Submitting),
                ("$r", claim.Revision)
            );
            if (changed == 1)
            {
                for (int index = 0; index < outcome.Recipients.Length; index++)
                {
                    RecipientEvidence recipient = outcome.Recipients[index];
                    Execute(
                        connection,
                        transaction,
                        "UPDATE SubmissionRecipients SET State=$s,ReplyCode=$c WHERE SubmissionId=$i AND Ordinal=$n;",
                        ("$s", (int)recipient.State),
                        ("$c", recipient.ReplyCode),
                        ("$i", claim.Submission.Id.ToString("N")),
                        ("$n", index)
                    );
                }
            }

            transaction.Commit();
            return changed == 1;
        }, cancellationToken);
    }

    public Task<bool> CompleteSentCopyAsync(Guid id, AccountId account, long revision, CancellationToken cancellationToken)
    {
        return WriteAsync(connection => Execute(
            connection,
            null,
            "UPDATE OutboxSubmissions SET State=$s,Revision=Revision+1 WHERE SubmissionId=$i AND AccountId=$a " +
            "AND Revision=$r AND State IN($accepted,$pending);",
            ("$s", (int)SubmissionState.Completed),
            ("$i", id.ToString("N")),
            ("$a", account.Value),
            ("$r", revision),
            ("$accepted", (int)SubmissionState.Accepted),
            ("$pending", (int)SubmissionState.SentCopyPending)
        ) == 1, cancellationToken);
    }

    public Task<OutboxEntry?> ClaimSentCopyAsync(Guid id, AccountId account, CancellationToken cancellationToken)
    {
        return WriteAsync<OutboxEntry?>(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                """
                UPDATE OutboxSubmissions SET State=$pending,Revision=Revision+1
                WHERE SubmissionId=$i AND AccountId=$a AND State=$accepted
                RETURNING Frozen,State,Revision,Evidence,SubmissionId,AccountId,BlobId;
                """,
                ("$pending", (int)SubmissionState.SentCopyPending),
                ("$accepted", (int)SubmissionState.Accepted),
                ("$i", id.ToString("N")),
                ("$a", account.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadOutbox(reader) : null;
        }, cancellationToken);
    }

    public Task RecoverAsync(CancellationToken cancellationToken)
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                UPDATE PendingOperations SET State=CASE WHEN json_extract(Payload,'$.Kind') IN(0,1)
                    THEN $queued ELSE $review END,
                    Evidence=CASE WHEN json_extract(Payload,'$.Kind') IN(0,1) THEN 0 ELSE $e END,
                    Revision=Revision+1 WHERE State=$c;
                """,
                ("$queued", (int)PendingState.Queued),
                ("$review", (int)PendingState.NeedsReview),
                ("$e", (int)Failure.NeedsReview),
                ("$c", (int)PendingState.InFlight)
            );
            Execute(
                connection,
                transaction,
                """
                UPDATE Messages SET Pending=COALESCE((SELECT State FROM PendingOperations o
                    WHERE o.AccountId=Messages.AccountId AND o.MessageId=Messages.MessageId
                    AND o.State IN(1,2,3,4,5) ORDER BY o.rowid DESC LIMIT 1),0)
                    WHERE (AccountId,MessageId) IN (SELECT AccountId,MessageId FROM PendingOperations);
                """
            );
            Execute(
                connection,
                transaction,
                "UPDATE OutboxSubmissions SET State=$s,Revision=Revision+1 WHERE State=$c;",
                ("$s", (int)SubmissionState.Uncertain),
                ("$c", (int)SubmissionState.Submitting)
            );
            Execute(
                connection,
                transaction,
                "UPDATE SubmissionRecipients SET State=$s WHERE SubmissionId IN " +
                "(SELECT SubmissionId FROM OutboxSubmissions WHERE State=$u) AND State=$n;",
                ("$s", (int)RecipientState.Uncertain),
                ("$u", (int)SubmissionState.Uncertain),
                ("$n", (int)RecipientState.NotAccepted)
            );
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writer.WaitAsync().ConfigureAwait(false);
        for (int index = 0; index < 4; index++)
        {
            await _readers.WaitAsync().ConfigureAwait(false);
        }

        _readers.Release(4);
        _writer.Release();
        _lease.Dispose();
        // Connection lifetimes belong to individual operations; pooling is disabled.
    }

    public Task<bool> ResolveAsync(OperationId id, AccountId account, bool retry, CancellationToken cancellationToken)
    {
        return ResolveOperationAsync(id, account, null, retry, cancellationToken);
    }

    public Task<bool> ResolveAsync(
        OperationId id,
        AccountId account,
        long revision,
        bool retry,
        CancellationToken cancellationToken
    )
    {
        return ResolveOperationAsync(id, account, revision, retry, cancellationToken);
    }

    public Task<ImmutableArray<CommandSnapshot>> CommandsAsync(AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT o.Payload,o.State,o.Revision,o.Attempts,o.Due,o.Evidence,o.OperationId,o.AccountId,o.MessageId," +
                "o.PayloadVersion,m.Subject FROM PendingOperations o LEFT JOIN Messages m ON m.AccountId=o.AccountId " +
                "AND m.MessageId=o.MessageId WHERE o.AccountId=$a ORDER BY o.rowid DESC LIMIT 200;",
                ("$a", account.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            ImmutableArray<CommandSnapshot>.Builder entries = ImmutableArray.CreateBuilder<CommandSnapshot>();
            while (reader.Read())
            {
                entries.Add(new CommandSnapshot(ReadOperation(reader), reader.IsDBNull(10) ? "" : reader.GetString(10)));
            }

            return entries.ToImmutable();
        }, cancellationToken);
    }

    public Task<string> ExplainSearchAsync(CancellationToken cancellationToken)
    {
        return ReadAsync(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "EXPLAIN QUERY PLAN SELECT MessageId FROM Messages WHERE AccountId=1 " +
                "AND Date<100000 ORDER BY Date DESC,MessageId DESC LIMIT 50;"
            );
            using SqliteDataReader reader = command.ExecuteReader();
            List<string> plans = new List<string>();
            while (reader.Read())
            {
                plans.Add(reader.GetString(3));
            }

            reader.Close();
            using SqliteCommand full = Command(
                connection,
                null,
                """
                EXPLAIN QUERY PLAN SELECT m.*,c.RemoteKey,i.UidValidity,i.Uid FROM Messages m
                LEFT JOIN ImapInstances i ON i.AccountId=m.AccountId AND i.MessageId=m.MessageId
                LEFT JOIN Collections c ON c.AccountId=i.AccountId AND c.FolderId=i.FolderId
                WHERE m.AccountId=1 AND (i.UidValidity IS NULL OR i.UidValidity=c.UidValidity)
                AND EXISTS(SELECT 1 FROM Memberships x WHERE x.AccountId=m.AccountId AND x.MessageId=m.MessageId)
                AND (m.Date<100000 OR (m.Date=100000 AND m.MessageId<100000))
                AND m.MessageId IN(SELECT rowid FROM SearchText WHERE SearchText MATCH '"résumé"')
                ORDER BY m.Date DESC,m.MessageId DESC LIMIT 50;
                """
            );
            using SqliteDataReader fullReader = full.ExecuteReader();
            plans.Add("Full FTS/join/keyset query:");
            while (fullReader.Read())
            {
                plans.Add(fullReader.GetString(3));
            }

            return string.Join("\n", plans);
        }, cancellationToken);
    }

    private Task<OutboxEntry?> ClaimSubmissionAsync(Guid? id, AccountId account, CancellationToken cancellationToken)
    {
        return WriteAsync<OutboxEntry?>(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            OutboxEntry? entry;
            using (SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT Frozen,State,Revision,Evidence,SubmissionId,AccountId,BlobId FROM OutboxSubmissions " +
                "WHERE AccountId=$a AND State=$s AND ($i IS NULL OR SubmissionId=$i) " +
                "ORDER BY Created,SubmissionId LIMIT 1;",
                ("$a", account.Value),
                ("$s", (int)SubmissionState.Queued),
                ("$i", id?.ToString("N"))
            ))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                entry = reader.Read() ? ReadOutbox(reader) : null;
            }

            if (entry is null)
            {
                return null;
            }

            Execute(
                connection,
                transaction,
                "UPDATE OutboxSubmissions SET State=$s,Revision=Revision+1 WHERE SubmissionId=$i;",
                ("$s", (int)SubmissionState.Submitting),
                ("$i", entry.Submission.Id.ToString("N"))
            );
            transaction.Commit();
            return entry with
            {
                State = SubmissionState.Submitting,
                Revision = entry.Revision + 1
            };
        }, cancellationToken);
    }

    private static ComposeIdentity? ReadIdentity(string json)
    {
        try
        {
            ComposeIdentity? identity = JsonSerializer.Deserialize<ComposeIdentity>(json);
            return identity is not null && identity.IsValid ? identity : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Capabilities? ReadCapabilities(string json)
    {
        if (json == "{}")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Capabilities>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    Task<OutboxEntry?> IOutboxStore.ReadAsync(Guid id, AccountId account, CancellationToken cancellationToken)
    {
        return ReadAsync<OutboxEntry?>(connection =>
        {
            using SqliteCommand command = Command(
                connection,
                null,
                "SELECT Frozen,State,Revision,Evidence,SubmissionId,AccountId,BlobId FROM OutboxSubmissions " +
                "WHERE SubmissionId=$i AND AccountId=$a;",
                ("$i", id.ToString("N")),
                ("$a", account.Value)
            );
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadOutbox(reader) : null;
        }, cancellationToken);
    }

    private Task<bool> ResolveOperationAsync(
        OperationId id,
        AccountId account,
        long? revision,
        bool retry,
        CancellationToken cancellationToken
    )
    {
        return WriteAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            OperationEntry? entry;
            using (SqliteCommand command = Command(
                connection,
                transaction,
                "SELECT Payload,State,Revision,Attempts,Due,Evidence,OperationId,AccountId,MessageId,PayloadVersion " +
                "FROM PendingOperations WHERE OperationId=$o AND AccountId=$a;",
                ("$o", id.Value.ToString("N")),
                ("$a", account.Value)
            ))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                entry = reader.Read() ? ReadOperation(reader) : null;
            }

            if (entry is null || (revision.HasValue && entry.Revision != revision.Value) ||
                entry.State is not (PendingState.NeedsAuthentication or PendingState.NeedsReview) ||
                (retry && !TargetMatches(connection, transaction, entry.Intent)))
            {
                return false;
            }

            PendingState state = retry ? PendingState.Queued : PendingState.Dismissed;
            Execute(
                connection,
                transaction,
                "UPDATE PendingOperations SET State=$s,Revision=Revision+1,Due=$d WHERE OperationId=$o AND AccountId=$a;",
                ("$s", (int)state),
                ("$d", Now()),
                ("$o", id.Value.ToString("N")),
                ("$a", account.Value)
            );
            Execute(
                connection,
                transaction,
                "UPDATE Messages SET Pending=$s WHERE AccountId=$a AND MessageId=$m;",
                ("$s", (int)state),
                ("$a", account.Value),
                ("$m", entry.Intent.Target.Value)
            );
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    private async Task<TValue> WriteAsync<TValue>(
        Func<SqliteConnection, TValue> action,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SqliteConnection connection = Connect();
                return action(connection);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 8 or 10 or 13 or 14)
        {
            throw new IOException("Local persistence is unavailable; unsent work remains protected.", exception);
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<TValue> ReadAsync<TValue>(
        Func<SqliteConnection, TValue> action,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _readers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using SqliteConnection connection = Connect();
                return action(connection);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readers.Release();
        }
    }

    private SqliteConnection Connect()
    {
        BlobStore.VerifyPath(_path);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            BlobStore.VerifyPath(_path + suffix);
        }
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private void RegisterBlob(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountId account,
        BlobReference blob
    )
    {
        if (blob.Account != account || blob.Id == Guid.Empty)
        {
            throw new ArgumentException("Object belongs to another account.");
        }

        string path = _blobs.ObjectPath(blob.Id);
        _blobs.ValidateOwnership(blob);
        BlobStore.VerifyPath(path);
        using FileStream stream = File.OpenRead(path);
        if (stream.Length != blob.Length || Convert.ToHexString(SHA256.HashData(stream)) != blob.Sha256)
        {
            throw new InvalidDataException("Object must be promoted and verified before reference commit.");
        }

        Execute(
            connection,
            transaction,
            "INSERT INTO Blobs VALUES($i,$a,$n,$l,$h,$p,$t) ON CONFLICT(BlobId) DO NOTHING;",
            ("$i", blob.Id.ToString("N")),
            ("$a", account.Value),
            ("$n", Path.GetFileName(path)),
            ("$l", blob.Length),
            ("$h", blob.Sha256),
            ("$p", blob.Protected),
            ("$t", Now())
        );
    }

    private static void PreserveCorruption(string path)
    {
        BlobStore.VerifyPath(path);
        string location = path + ".recovery-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(location);
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                BlobStore.VerifyPath(path + suffix);
                File.Copy(path + suffix, Path.Combine(location, "profile.db" + suffix));
            }
        }

        // Protected objects remain in place; recovery never restores or replays queues.
    }

    private long Now()
    {
        // All persisted timestamps use UTC Unix milliseconds.
        return _time.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private static void Reindex(SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        Execute(connection, transaction, "DELETE FROM SearchText WHERE rowid=$i;", ("$i", id));
        Execute(
            connection,
            transaction,
            """
            INSERT INTO SearchText(rowid,Subject,Participants,Body)
            SELECT m.MessageId,m.Subject,m.Participants,COALESCE(b.Text,'') FROM Messages m
            LEFT JOIN Bodies b ON b.AccountId=m.AccountId AND b.MessageId=m.MessageId WHERE m.MessageId=$i;
            """,
            ("$i", id)
        );
    }

    private static bool TargetMatches(SqliteConnection connection, SqliteTransaction transaction, MutationIntent intent)
    {
        return TargetMatches(connection, transaction, intent.Account, intent.Target, intent.Provider);
    }

    private static bool TargetMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountId account,
        MessageId target,
        ProviderIdentity provider
    )
    {
        long count = Convert.ToInt64(
            Scalar(
    connection,
    transaction,
    """
            SELECT count(*) FROM Messages m WHERE m.AccountId=$a AND m.MessageId=$m AND m.ProviderKey=$k
            AND EXISTS(SELECT 1 FROM Memberships x WHERE x.AccountId=m.AccountId AND x.MessageId=m.MessageId)
            """,
    ("$a", account.Value),
    ("$m", target.Value),
    ("$k", provider.Key)
),
            CultureInfo.InvariantCulture
        );
        ImapIdentity? imap = provider.Imap;
        if (count != 1 || imap is null)
        {
            return count == 1;
        }

        return Convert.ToInt64(Scalar(
            connection,
            transaction,
            """
            SELECT count(*) FROM ImapInstances i JOIN Collections c USING(AccountId,FolderId)
            WHERE i.AccountId=$a AND i.MessageId=$m AND c.RemoteKey=$k
            AND i.UidValidity=$v AND c.UidValidity=$v AND i.Uid=$u;
            """,
            ("$a", account.Value),
            ("$m", target.Value),
            ("$k", imap.Mailbox),
            ("$v", imap.UidValidity),
            ("$u", imap.Uid)
        ), CultureInfo.InvariantCulture) == 1;
    }

    private static MessageSnapshot ReadMessage(SqliteDataReader reader)
    {
        AccountId account = new AccountId(reader.GetInt64(1));
        ImapIdentity? imap = reader.IsDBNull(13) ? null :
            new ImapIdentity(
                account,
                reader.GetString(13),
                checked((uint)reader.GetInt64(14)),
                checked((uint)reader.GetInt64(15))
            );
        return new MessageSnapshot(
            account,
            new MessageId(reader.GetInt64(0)),
            new ProviderIdentity(account, reader.GetString(2), imap),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
            reader.GetInt64(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            (ContentAvailability)reader.GetInt32(10),
            (PendingState)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(16) ? null : reader.GetString(16).Replace('\r', ' ').Replace('\n', ' '),
            reader.GetBoolean(17)
        );
    }

    private static OperationEntry ReadOperation(SqliteDataReader reader)
    {
        MutationIntent intent = JsonSerializer.Deserialize<MutationIntent>(reader.GetString(0)) ??
            throw new InvalidDataException("Missing operation payload.");
        if (intent.Id.Value.ToString("N") != reader.GetString(6) || intent.Account.Value != reader.GetInt64(7) ||
            intent.Target.Value != reader.GetInt64(8) || intent.PayloadVersion != reader.GetInt32(9) ||
            intent.Provider.Account != intent.Account || intent.Provider.Imap is not null &&
                intent.Provider.Imap.Account != intent.Account || !Enum.IsDefined(intent.Kind))
        {
            throw new InvalidDataException("Operation payload does not match its durable identity.");
        }

        return new OperationEntry(
            intent,
            (PendingState)reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            (Failure)reader.GetInt32(5)
        );
    }

    private static OutboxEntry ReadOutbox(SqliteDataReader reader)
    {
        FrozenSubmission submission = JsonSerializer.Deserialize<FrozenSubmission>(reader.GetString(0)) ??
            throw new InvalidDataException("Missing frozen submission.");
        if (submission.Id.ToString("N") != reader.GetString(4) ||
            submission.Account.Value != reader.GetInt64(5) || submission.Mime.Account != submission.Account ||
            submission.Mime.Id.ToString("N") != reader.GetString(6) || !submission.Mime.Protected ||
            submission.Recipients.IsDefaultOrEmpty ||
            submission.Recipients.Distinct(StringComparer.OrdinalIgnoreCase).Count() != submission.Recipients.Length)
        {
            throw new InvalidDataException("Frozen submission does not match its durable identity.");
        }

        return new OutboxEntry(
            submission,
            (SubmissionState)reader.GetInt32(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<SubmissionOutcome>(reader.GetString(3))
        );
    }

    private static DraftSnapshot? ReadDraft(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        AccountId account
    )
    {
        using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT d.*,b.Length,b.Hash,b.Protected FROM Drafts d JOIN Blobs b USING(AccountId,BlobId) " +
            "WHERE DraftId=$i AND d.AccountId=$a;",
            ("$i", id.ToString("N")),
            ("$a", account.Value)
        );
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new DraftSnapshot(
            id,
            account,
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            new BlobReference(
    Guid.ParseExact(reader.GetString(6), "N"),
    account,
    reader.GetInt64(11),
    reader.GetString(12),
    reader.GetBoolean(13)
),
            reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<ProviderIdentity>(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10)
        );
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] values
    )
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] values
    )
    {
        using SqliteCommand command = Command(connection, transaction, sql, values);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] values
    )
    {
        using SqliteCommand command = Command(connection, transaction, sql, values);
        return command.ExecuteScalar();
    }
}

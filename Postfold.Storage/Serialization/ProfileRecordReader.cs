using Microsoft.Data.Sqlite;
using Postfold.Core.Enums;
using Postfold.Core.Models.Accounts;
using Postfold.Core.Models.Drafts;
using Postfold.Core.Models.Messages;
using Postfold.Core.Models.Operations;
using Postfold.Core.Models.Outbox;
using Postfold.Core.Models.Synchronisation;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Postfold.Storage.Serialization;

/// <summary>Decodes persisted records and checks their durable identities before use.</summary>
internal static class ProfileRecordReader
{
    internal static ComposeIdentity? ReadIdentity(
        string json
    )
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

    internal static Capabilities? ReadCapabilities(
        string json
    )
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

    internal static GoogleAccountBinding? ReadGoogleBinding(
        string json
    )
    {
        try
        {
            GoogleAccountBinding? binding = JsonSerializer.Deserialize<GoogleAccountBinding>(json);
            return binding is { IsValid: true } ? binding : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static MicrosoftAccountBinding? ReadMicrosoftBinding(
        string json
    )
    {
        try
        {
            MicrosoftAccountBinding? binding = JsonSerializer.Deserialize<MicrosoftAccountBinding>(json);
            return binding is not null && binding.IsValid ? binding : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static MessageSnapshot ReadMessage(
        SqliteDataReader reader
    )
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

    internal static OperationEntry ReadOperation(
        SqliteDataReader reader
    )
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

    internal static OutboxEntry ReadOutbox(
        SqliteDataReader reader
    )
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
}

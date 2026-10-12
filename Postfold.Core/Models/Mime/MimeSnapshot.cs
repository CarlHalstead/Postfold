using Postfold.Core.Models.Messages;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Mime;

public sealed record MimeSnapshot(
    string Subject,
    string Participants,
    string? MessageIdHeader,
    string? PlainText,
    string? Html,
    ImmutableArray<AttachmentSnapshot> Attachments,
    ImmutableArray<string> ReplyRecipients = default
);

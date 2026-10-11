using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record MimeSnapshot(string Subject, string Participants, string? MessageIdHeader,
    string? PlainText, string? Html, ImmutableArray<AttachmentSnapshot> Attachments);

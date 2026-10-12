using System;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Drafts;

public sealed record ComposeContent(
    string Sender,
    ImmutableArray<string> Recipients,
    string Subject,
    string PlainText,
    DateTimeOffset Date,
    string MessageId,
    ImmutableArray<ComposeAttachment> Attachments = default,
    string Signature = "",
    Guid? Predecessor = null
);

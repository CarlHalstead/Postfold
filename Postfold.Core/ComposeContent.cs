using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Postfold.Core;

public sealed record ComposeContent(string Sender, ImmutableArray<string> Recipients, string Subject,
    string PlainText, DateTimeOffset Date, string MessageId,
    ImmutableArray<ComposeAttachment> Attachments = default, string Signature = "", Guid? Predecessor = null);

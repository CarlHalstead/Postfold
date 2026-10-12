using Postfold.Core.Models.Messages;
using System.Collections.Immutable;

namespace Postfold.Core.Models.Mime;

public sealed record MimeHtmlDocument(string Html, ImmutableArray<InlineImage> Images);

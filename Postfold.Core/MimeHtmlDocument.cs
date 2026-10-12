using System.Collections.Immutable;

namespace Postfold.Core;

public sealed record MimeHtmlDocument(string Html, ImmutableArray<InlineImage> Images);

using System.Collections.Immutable;

namespace Postfold.UI;

public sealed record HtmlRenderPlan(string Html, ImmutableArray<HtmlImageRequest> RemoteImages,
    ImmutableArray<string> CidResourceIds);

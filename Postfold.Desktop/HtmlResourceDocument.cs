using System.Collections.Generic;
using Postfold.UI;

namespace Postfold.Desktop;

internal sealed record HtmlResourceDocument(HtmlRenderPlan Plan, Dictionary<string, byte[]> Resources);

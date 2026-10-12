using Postfold.UI.Models;
using System.Collections.Generic;

namespace Postfold.Desktop.Models;

internal sealed record HtmlResourceDocument(HtmlRenderPlan Plan, Dictionary<string, byte[]> Resources);

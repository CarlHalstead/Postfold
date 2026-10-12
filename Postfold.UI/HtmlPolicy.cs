using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using Postfold.Core;

namespace Postfold.UI;

public static class HtmlPolicy
{
    public const string Csp = "default-src 'none'; script-src 'none'; object-src 'none'; frame-src 'none'; " +
        "connect-src 'none'; base-uri 'none'; form-action 'none'; img-src 'none'; style-src 'none'";

    public static Outcome<string> Sanitise(string html, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(html) > maximumBytes)
        {
            return Outcome<string>.Fail(Failure.Validation);
        }

        HtmlSanitizer sanitizer = CreateSanitizer(false);
        string clean = sanitizer.Sanitize(html);
        return Outcome<string>.Success("<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + Csp + "\"></head><body>" + clean + "</body></html>");
    }

    public static Outcome<HtmlRenderPlan> Plan(string html, Guid context, IReadOnlyDictionary<string, string> cidResources)
    {
        if (Encoding.UTF8.GetByteCount(html) > 262144 || cidResources.Count > 32)
        {
            return Outcome<HtmlRenderPlan>.Fail(Failure.Validation);
        }

        HtmlSanitizer sanitizer = CreateSanitizer(true);
        using AngleSharp.Html.Dom.IHtmlDocument document = new HtmlParser().ParseDocument(sanitizer.Sanitize(html));
        IHtmlCollection<IElement> elements = document.QuerySelectorAll("*");
        if (elements.Length > 4096)
        {
            return Outcome<HtmlRenderPlan>.Fail(Failure.Validation);
        }

        ImmutableArray<HtmlImageRequest>.Builder requests = ImmutableArray.CreateBuilder<HtmlImageRequest>();
        HashSet<string> selectedCid = new HashSet<string>(StringComparer.Ordinal);
        int imageCount = 0;
        string origin = "https://postfold.invalid/" + context.ToString("N") + "/";
        foreach (IElement element in elements)
        {
            string? source = element.GetAttribute("src");
            element.RemoveAttribute("src");
            if (element.LocalName != "img" || source is null)
            {
                continue;
            }

            if (++imageCount > 32)
            {
                return Outcome<HtmlRenderPlan>.Fail(Failure.Validation);
            }

            if (source.Length <= 512 && source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                string contentId = Uri.UnescapeDataString(source[4..]);
                if (cidResources.TryGetValue(contentId, out string? identifier) && identifier.Length is > 0 and <= 64 &&
                    System.Linq.Enumerable.All(identifier, char.IsAsciiLetterOrDigit))
                {
                    element.SetAttribute("src", origin + identifier);
                    selectedCid.Add(identifier);
                }
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out Uri? url) && url.Scheme == Uri.UriSchemeHttps &&
                url.Port == 443 && url.UserInfo.Length == 0 && url.Fragment.Length == 0 && source.Length <= 2048 &&
                requests.Count < 32)
            {
                string identifier = Guid.NewGuid().ToString("N");
                requests.Add(new HtmlImageRequest(identifier, url));
                element.SetAttribute("src", origin + identifier);
            }
        }

        string csp = Csp.Replace("img-src 'none'", "img-src " + origin, StringComparison.Ordinal);
        string result = "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\"></head><body>" +
            document.Body!.InnerHtml + "</body></html>";
        return Encoding.UTF8.GetByteCount(result) > 262144 ? Outcome<HtmlRenderPlan>.Fail(Failure.Validation) :
            Outcome<HtmlRenderPlan>.Success(new HtmlRenderPlan(result, requests.ToImmutable(), selectedCid.ToImmutableArray()));
    }

    private static HtmlSanitizer CreateSanitizer(bool images)
    {
        HtmlSanitizer sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedSchemes.Clear();
        foreach (string tag in new[] { "p", "br", "div", "span", "b", "strong", "i", "em", "ul", "ol", "li",
            "table", "tbody", "thead", "tr", "td", "th", "blockquote", "pre", "code", "h1", "h2", "h3" })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        if (images)
        {
            sanitizer.AllowedTags.Add("img");
            sanitizer.AllowedAttributes.Add("src");
            sanitizer.AllowedAttributes.Add("alt");
            sanitizer.AllowedSchemes.Add("cid");
            sanitizer.AllowedSchemes.Add("https");
        }

        return sanitizer;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Vantage.Domain;

namespace Vantage.Infrastructure.GenAi;

/// <summary>What the checker found. <see cref="Errors"/> block the upload; <see cref="Warnings"/> are shown but don't.</summary>
public sealed record GenAiCheckResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> Libraries, long SizeBytes)
{
    public bool Passed => Errors.Count == 0;
}

/// <summary>
/// Checks a GenAI dashboard file before it is stored: one UTF-8 HTML page, within the size limit, built from the
/// approved starter template (it carries the template marker), and loading libraries only from approved CDNs over
/// HTTPS. Nothing may point at another file, because the page is served on its own. The checks are a first line of
/// defence; the strict Content-Security-Policy and the sandbox applied when the file is served are the real ones.
/// </summary>
public static partial class GenAiChecker
{
    public const string TemplateMetaName = "vantage-template";
    public const string TemplateVersion = "genai-1";

    public static GenAiCheckResult Check(byte[] bytes, IReadOnlyCollection<string> approvedHosts)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var libraries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bytes.Length == 0) return new([ "The file is empty." ], warnings, [], 0);
        if (bytes.Length > Rules.GenAiMaxBytes)
            errors.Add($"The file is {bytes.Length / 1024d / 1024d:0.#} MB, over the {Rules.GenAiMaxBytes / 1024 / 1024} MB limit.");
        else if (bytes.Length >= Rules.GenAiWarnBytes)
            warnings.Add($"The file is {bytes.Length / 1024d / 1024d:0.#} MB. Files over {Rules.GenAiWarnBytes / 1024 / 1024} MB load slowly; consider trimming embedded data.");

        string html;
        try { html = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            errors.Add("The file isn't UTF-8 text. Save it as UTF-8 HTML.");
            return new(errors, warnings, [], bytes.Length);
        }

        if (!HtmlTag().IsMatch(html)) errors.Add("This doesn't look like an HTML page. It needs an <html> element.");
        if (!HasTemplateMarker(html))
            errors.Add($"The file isn't built from the approved template. Start from the Vantage starter template, which carries <meta name=\"{TemplateMetaName}\" content=\"{TemplateVersion}\">.");

        if (Forbidden().Match(html) is { Success: true } f)
            errors.Add($"<{f.Groups[1].Value.ToLowerInvariant()}> isn't allowed. A dashboard must be one self-contained page.");
        if (MetaRefresh().IsMatch(html)) errors.Add("A page can't redirect itself (<meta http-equiv=\"refresh\">).");

        var approved = new HashSet<string>(approvedHosts, StringComparer.OrdinalIgnoreCase);
        foreach (var url in ExternalUrls(html).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var u = url.Trim();
            if (u.Length == 0 || u.StartsWith('#') || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) continue;
            if (u.StartsWith("//")) u = "https:" + u;
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add($"“{Shorten(url)}” points at another file. Everything must be inside the one HTML file; use a CDN library or embed the data.");
                continue;
            }
            if (uri.Scheme != "https") { errors.Add($"“{Shorten(url)}” isn't HTTPS. Libraries must load over HTTPS."); continue; }
            if (!approved.Contains(uri.Host)) { errors.Add($"{uri.Host} isn't an approved CDN. Approved: {(approved.Count == 0 ? "none yet" : string.Join(", ", approved.Order()))}."); continue; }
            libraries.Add(uri.Host);
        }

        if (NetworkCalls().IsMatch(html))
            warnings.Add("The page uses fetch, XMLHttpRequest or WebSocket. Network calls are blocked when a dashboard runs, so put its data in the file.");

        return new(errors.Distinct().ToList(), warnings, libraries.ToList(), bytes.Length);
    }

    public static bool HasTemplateMarker(string html) =>
        MetaTag().Matches(html).Any(m => NameAttr().IsMatch(m.Value) && ContentAttr().IsMatch(m.Value));

    /// <summary>URLs the page loads from: src, href (on link only), poster, srcset, CSS url() and @import.</summary>
    private static IEnumerable<string> ExternalUrls(string html)
    {
        foreach (Match tag in LoadingTag().Matches(html))
        {
            var isLink = tag.Groups[1].Value.Equals("link", StringComparison.OrdinalIgnoreCase);
            foreach (Match a in UrlAttr().Matches(tag.Value))
            {
                var attr = a.Groups[1].Value.ToLowerInvariant();
                if (attr == "href" && !isLink && !tag.Groups[1].Value.Equals("use", StringComparison.OrdinalIgnoreCase)) continue;
                var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                if (attr == "srcset") { foreach (var part in value.Split(',')) yield return part.Trim().Split(' ')[0]; }
                else yield return value;
            }
        }
        foreach (Match m in CssUrl().Matches(html)) yield return m.Groups[2].Value;
        foreach (Match m in CssImport().Matches(html)) yield return m.Groups[2].Value;
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";

    [GeneratedRegex(@"<html\b", RegexOptions.IgnoreCase)] private static partial Regex HtmlTag();
    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex MetaTag();
    [GeneratedRegex("name\\s*=\\s*[\"']?" + TemplateMetaName + "[\"']?(?=[\\s/>\"'])", RegexOptions.IgnoreCase)] private static partial Regex NameAttr();
    [GeneratedRegex("content\\s*=\\s*[\"']?" + TemplateVersion + "[\"']?(?=[\\s/>\"'])", RegexOptions.IgnoreCase)] private static partial Regex ContentAttr();
    [GeneratedRegex(@"<(iframe|frame|frameset|object|embed|base|applet)\b", RegexOptions.IgnoreCase)] private static partial Regex Forbidden();
    [GeneratedRegex(@"<meta\b[^>]*http-equiv\s*=\s*[""']?refresh", RegexOptions.IgnoreCase)] private static partial Regex MetaRefresh();
    [GeneratedRegex(@"<(script|link|img|source|video|audio|track|input|image|use)\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex LoadingTag();
    [GeneratedRegex(@"\b(src|href|poster|srcset)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))", RegexOptions.IgnoreCase)] private static partial Regex UrlAttr();
    [GeneratedRegex(@"url\(\s*(['""]?)([^)'""]+)\1\s*\)", RegexOptions.IgnoreCase)] private static partial Regex CssUrl();
    [GeneratedRegex(@"@import\s+(?:url\(\s*)?(['""])([^'""]+)\1", RegexOptions.IgnoreCase)] private static partial Regex CssImport();
    [GeneratedRegex(@"\b(fetch\s*\(|XMLHttpRequest|WebSocket\s*\(|EventSource\s*\()")] private static partial Regex NetworkCalls();
}

/// <summary>The approved starter template, shipped inside the API so authors can download it from the Publish page.</summary>
public static class GenAiTemplate
{
    public const string FileName = "vantage-genai-starter-template.html";

    public static byte[] Read()
    {
        using var s = typeof(GenAiTemplate).Assembly.GetManifestResourceStream("starter-template.html")
            ?? throw new InvalidOperationException("The starter template is missing from the build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

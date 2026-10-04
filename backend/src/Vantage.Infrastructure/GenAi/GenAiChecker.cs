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
/// Vets a GenAI dashboard file before it is stored. A file passes only if it is:
/// one UTF-8 HTML page within the size limit, built from the approved starter template, loading scripts and
/// stylesheets only from approved CDNs over HTTPS at an exact version with a Subresource Integrity hash, linking to
/// nothing else, and free of the code patterns that exist to escape a page's sandbox or reach the network.
/// These checks give publishers early, specific feedback. They are not the last line of defence: a determined author
/// can obfuscate code past any pattern, which is why the file is also malware-scanned (when a scanner is configured)
/// and always served from its own origin, in a sandbox, under a strict Content-Security-Policy that has no
/// 'unsafe-eval' and allows no network calls. See docs/genai-security.md.
/// </summary>
public static partial class GenAiChecker
{
    public const string TemplateMetaName = "vantage-template";
    public const string TemplateVersion = "genai-1";

    /// <summary>Code that runs text as code, reaches outside the frame, or talks to the network. Each is refused with its message.</summary>
    private static readonly (Regex Pattern, string Message)[] Banned =
    [
        (EvalCall(), "eval() runs text as code, which isn't allowed."),
        (FunctionCtor(), "new Function() runs text as code, which isn't allowed."),
        (StringTimer(), "setTimeout/setInterval with text instead of a function runs text as code, which isn't allowed."),
        (DocumentWrite(), "document.write() can inject script, which isn't allowed."),
        (DocumentCookie(), "document.cookie isn't allowed."),
        (WindowOpen(), "window.open() isn't allowed."),
        (ReachParent(), "Reaching the parent or top window (window.top, window.parent, window.opener) isn't allowed."),
        (Navigate(), "Navigating the page (location.href =, location.assign(), location.replace()) isn't allowed."),
        (JavascriptUrl(), "javascript: URLs aren't allowed."),
        (WorkersWasm(), "Workers, service workers, importScripts and WebAssembly aren't allowed."),
        (DynamicImport(), "Dynamic import() isn't allowed. Load libraries with a <script> tag from an approved CDN."),
        (PostMessage(), "postMessage() isn't allowed."),
        (SendBeacon(), "navigator.sendBeacon() isn't allowed."),
        (NetworkCalls(), "Network calls (fetch, XMLHttpRequest, WebSocket, EventSource) aren't allowed. Put the data in the file."),
    ];

    private static readonly HashSet<string> ScriptTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text/javascript", "application/javascript", "module", "application/json", "application/ld+json", "text/plain",
    };

    public static GenAiCheckResult Check(byte[] bytes, IReadOnlyCollection<string> approvedHosts, int warnBytes = Rules.GenAiWarnBytes)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var libraries = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (bytes.Length == 0) return new(["The file is empty."], warnings, [], 0);
        if (bytes.Length > Rules.GenAiMaxBytes)
            errors.Add($"The file is {bytes.Length / 1024d / 1024d:0.#} MB, over the {Rules.GenAiMaxBytes / 1024 / 1024} MB limit.");
        else if (bytes.Length >= warnBytes)
            warnings.Add($"The file is {bytes.Length / 1024d / 1024d:0.#} MB. Files over {warnBytes / 1024d / 1024d:0.#} MB load slowly; consider trimming embedded data.");

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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Url(string url, bool needsIntegrity, Dictionary<string, string> attrs, string what)
        {
            var u = url.Trim();
            if (u.Length == 0 || u.StartsWith('#') || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
            if (u.StartsWith("//")) u = "https:" + u;
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                if (seen.Add("rel:" + url)) errors.Add($"“{Shorten(url)}” points at another file. Everything must be inside the one HTML file; use an approved CDN library or embed the data.");
                return;
            }
            if (uri.Scheme != "https") { if (seen.Add("http:" + url)) errors.Add($"“{Shorten(url)}” isn't HTTPS. Libraries must load over HTTPS."); return; }
            if (!approved.Contains(uri.Host)) { if (seen.Add("host:" + uri.Host)) errors.Add($"{uri.Host} isn't an approved CDN. Approved: {(approved.Count == 0 ? "none yet" : string.Join(", ", approved.Order()))}."); return; }
            if (!needsIntegrity) { libraries.Add(uri.Host); return; }

            // Scripts and stylesheets are code: exact version and an integrity hash, so a changed or tampered file won't run.
            var lib = PinnedLibrary(uri);
            if (lib is null) { if (seen.Add("ver:" + url)) errors.Add($"“{Shorten(url)}” isn't pinned to an exact version. Use a URL like https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js or https://cdnjs.cloudflare.com/ajax/libs/d3/7.9.0/d3.min.js."); }
            else libraries.Add(lib);
            if (!attrs.TryGetValue("integrity", out var integrity) || !ValidIntegrity(integrity))
                { if (seen.Add("sri:" + url)) errors.Add($"{what} {Shorten(url)} needs an integrity=\"sha384-…\" attribute (Subresource Integrity), so the browser refuses it if the file changes."); }
            if (!attrs.TryGetValue("crossorigin", out var cors) || !(cors.Length == 0 || cors.Equals("anonymous", StringComparison.OrdinalIgnoreCase)))
                { if (seen.Add("cors:" + url)) errors.Add($"{what} {Shorten(url)} needs crossorigin=\"anonymous\" next to its integrity attribute."); }
        }

        foreach (Match m in StartTag().Matches(html))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var attrs = Attributes(m.Value);
            switch (name)
            {
                case "script":
                    if (attrs.TryGetValue("type", out var type) && !ScriptTypes.Contains(type.Trim()))
                        errors.Add($"<script type=\"{Shorten(type)}\"> isn't allowed (import maps and speculation rules can load code from elsewhere).");
                    if (attrs.TryGetValue("src", out var ssrc)) Url(ssrc, true, attrs, "The script");
                    break;
                case "link":
                    var rel = attrs.GetValueOrDefault("rel", "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var href = attrs.GetValueOrDefault("href", "");
                    if (rel.Contains("stylesheet")) Url(href, true, attrs, "The stylesheet");
                    else if (rel.Contains("icon") && href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { }
                    else errors.Add($"<link rel=\"{Shorten(string.Join(' ', rel))}\"> isn't allowed. Only stylesheets from approved CDNs (with integrity) are.");
                    break;
                case "a":
                    if (attrs.TryGetValue("href", out var ahref) && (ahref.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase) || ahref.TrimStart().StartsWith("//")))
                        errors.Add($"A link to {Shorten(ahref)} isn't allowed. A dashboard page can't send people to other sites.");
                    break;
                default:
                    foreach (var key in new[] { "src", "href", "poster" })
                        if (attrs.TryGetValue(key, out var v)) Url(v, false, attrs, "The file");
                    if (attrs.TryGetValue("srcset", out var srcset))
                        foreach (var part in srcset.Split(',')) Url(part.Trim().Split(' ')[0], false, attrs, "The image");
                    break;
            }
        }

        foreach (Match m in CssUrl().Matches(html)) Url(m.Groups[2].Value, false, [], "The file");
        if (CssImport().IsMatch(html)) errors.Add("CSS @import isn't allowed. Add the stylesheet as a <link> with an integrity attribute instead.");

        foreach (var (pattern, message) in Banned)
            if (pattern.IsMatch(html)) errors.Add(message);

        if (LongEncodedBlob(html) is { } blobChars)
            warnings.Add($"The page contains a long encoded text blob (about {blobChars / 1024} KB). Encoded text can hide code; make sure it is data.");
        if (Atob().IsMatch(html)) warnings.Add("The page decodes base64 text with atob(). Make sure it decodes data, not code.");

        return new(errors.Distinct().ToList(), warnings, libraries.ToList(), bytes.Length);
    }

    public static bool HasTemplateMarker(string html)
    {
        var outsideComments = HtmlComment().Replace(html, " "); // a marker hidden in a comment doesn't count
        return MetaTag().Matches(outsideComments).Any(m => NameAttr().IsMatch(m.Value) && ContentAttr().IsMatch(m.Value));
    }

    /// <summary>The library an approved-CDN URL points at when it names an exact version (e.g. "chart.js@4.4.7 (cdn.jsdelivr.net)"), else null.</summary>
    private static string? PinnedLibrary(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (uri.Host.Equals("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase))
        {
            var m = JsdelivrPinned().Match(path);
            return m.Success ? $"{m.Groups[1].Value}@{m.Groups[2].Value} ({uri.Host})" : null;
        }
        if (uri.Host.Equals("cdnjs.cloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            var m = CdnjsPinned().Match(path);
            return m.Success ? $"{m.Groups[1].Value}@{m.Groups[2].Value} ({uri.Host})" : null;
        }
        return uri.Host; // other approved hosts: no version rule we can check, the integrity hash still applies
    }

    /// <summary>At least one sha256, sha384 or sha512 hash whose base64 has the right length.</summary>
    private static bool ValidIntegrity(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(h =>
        {
            var m = IntegrityHash().Match(h);
            if (!m.Success) return false;
            var expected = m.Groups[1].Value switch { "sha256" => 32, "sha384" => 48, _ => 64 };
            try { return Convert.FromBase64String(m.Groups[2].Value).Length == expected; }
            catch (FormatException) { return false; }
        });

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attribute().Matches(tag[1..]).Skip(1)) // the first match is the tag name
            result.TryAdd(a.Groups[1].Value, a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
        return result;
    }

    /// <summary>The size of the longest base64-looking run that isn't a data: URL, if it is large enough to hide code.</summary>
    private static int? LongEncodedBlob(string html)
    {
        foreach (Match m in EncodedRun().Matches(html))
        {
            var before = html[Math.Max(0, m.Index - 12)..m.Index];
            if (!before.Contains("base64,", StringComparison.OrdinalIgnoreCase)) return m.Length;
        }
        return null;
    }

    private static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "...";

    [GeneratedRegex(@"<html\b", RegexOptions.IgnoreCase)] private static partial Regex HtmlTag();
    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex HtmlComment();
    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex MetaTag();
    [GeneratedRegex("name\\s*=\\s*[\"']?" + TemplateMetaName + "[\"']?(?=[\\s/>\"'])", RegexOptions.IgnoreCase)] private static partial Regex NameAttr();
    [GeneratedRegex("content\\s*=\\s*[\"']?" + TemplateVersion + "[\"']?(?=[\\s/>\"'])", RegexOptions.IgnoreCase)] private static partial Regex ContentAttr();
    [GeneratedRegex(@"<(iframe|frame|frameset|object|embed|base|applet)\b", RegexOptions.IgnoreCase)] private static partial Regex Forbidden();
    [GeneratedRegex(@"<meta\b[^>]*http-equiv\s*=\s*[""']?refresh", RegexOptions.IgnoreCase)] private static partial Regex MetaRefresh();
    // A start tag, skipping over quoted attribute values so a ">" inside one can't hide later attributes.
    [GeneratedRegex(@"<(script|link|img|source|video|audio|track|input|image|use|a)\b(?:[^>""']|""[^""]*""|'[^']*')*>", RegexOptions.IgnoreCase)] private static partial Regex StartTag();
    [GeneratedRegex(@"([^\s""'<>/=]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?")] private static partial Regex Attribute();
    [GeneratedRegex(@"url\(\s*(['""]?)([^)'""]+)\1\s*\)", RegexOptions.IgnoreCase)] private static partial Regex CssUrl();
    [GeneratedRegex(@"@import\b", RegexOptions.IgnoreCase)] private static partial Regex CssImport();
    [GeneratedRegex(@"^/(?:npm|gh)/((?:@[^/@]+/)?[^/@]+)@(\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)/", RegexOptions.IgnoreCase)] private static partial Regex JsdelivrPinned();
    [GeneratedRegex(@"^/ajax/libs/([^/]+)/(\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.\-]+)?)/", RegexOptions.IgnoreCase)] private static partial Regex CdnjsPinned();
    [GeneratedRegex(@"^(sha256|sha384|sha512)-([A-Za-z0-9+/]+={0,2})$")] private static partial Regex IntegrityHash();
    [GeneratedRegex(@"[A-Za-z0-9+/]{4000,}={0,2}")] private static partial Regex EncodedRun();
    [GeneratedRegex(@"\batob\s*\(")] private static partial Regex Atob();

    [GeneratedRegex(@"(?<![\w$.])eval\s*\(")] private static partial Regex EvalCall();
    [GeneratedRegex(@"\bnew\s+Function\b|(?<![\w$.])Function\s*\(")] private static partial Regex FunctionCtor();
    [GeneratedRegex(@"\b(?:setTimeout|setInterval)\s*\(\s*[""'`]")] private static partial Regex StringTimer();
    [GeneratedRegex(@"\bdocument\s*\.\s*write(?:ln)?\s*\(")] private static partial Regex DocumentWrite();
    [GeneratedRegex(@"\bdocument\s*\.\s*cookie\b")] private static partial Regex DocumentCookie();
    [GeneratedRegex(@"\b(?:window|self|globalThis)\s*\.\s*open\s*\(")] private static partial Regex WindowOpen();
    [GeneratedRegex(@"\b(?:window|self|globalThis)\s*\.\s*(?:top|parent|opener)\b|(?<![\w$.])(?:top|parent|opener)\s*\.\s*(?:location|document|postMessage|frames)\b")] private static partial Regex ReachParent();
    [GeneratedRegex(@"(?<![\w$])location\s*(?:=(?!=)|\.\s*(?:href\s*=(?!=)|assign\s*\(|replace\s*\())")] private static partial Regex Navigate();
    [GeneratedRegex(@"[""'=]\s*javascript\s*:", RegexOptions.IgnoreCase)] private static partial Regex JavascriptUrl();
    [GeneratedRegex(@"\bimportScripts\s*\(|\bnew\s+(?:Shared)?Worker\s*\(|\bserviceWorker\b|\bWebAssembly\b")] private static partial Regex WorkersWasm();
    [GeneratedRegex(@"(?<![\w$.])import\s*\(")] private static partial Regex DynamicImport();
    [GeneratedRegex(@"\.\s*postMessage\s*\(")] private static partial Regex PostMessage();
    [GeneratedRegex(@"\bnavigator\s*\.\s*sendBeacon\b")] private static partial Regex SendBeacon();
    [GeneratedRegex(@"\bfetch\s*\(|\bXMLHttpRequest\b|\bWebSocket\s*\(|\bEventSource\s*\(")] private static partial Regex NetworkCalls();
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

using System.Text.RegularExpressions;
using Vantage.Infrastructure.Services;

namespace Vantage.Infrastructure.Embedding;

/// <summary>A Tableau view the portal can embed, with the address it should be embedded from.</summary>
/// <param name="IsPublic">True for Tableau Public (no tenant, no sign-in token); false for a Tableau Server site.</param>
/// <param name="Src">The address given to the Embedding API (https, no query or fragment).</param>
public sealed record TableauView(bool IsPublic, string Src, string Host, string Workbook, string View, string? Site);

/// <summary>
/// Reads the address of a Tableau view and turns it into the form the Embedding API wants. Pure code, so it is tested without a server.
/// Without a server tenant the view must be on Tableau Public (public.tableau.com); with one it must be on that server and its site.
/// Accepted forms: Tableau Public <c>/views/Workbook/View</c> and <c>/app/profile/{user}/viz/Workbook/View</c>; Tableau Server
/// <c>/views/…</c>, <c>/t/{site}/views/…</c> and the browser forms <c>/#/views/…</c> and <c>/#/site/{site}/views/…</c>.
/// </summary>
public static class TableauViewUrl
{
    public const string PublicHost = "public.tableau.com";
    public const int MaxLength = 250;
    private static readonly Regex Segment = new("^[A-Za-z0-9_.~%\\-]{1,200}$", RegexOptions.Compiled);

    /// <param name="serverUrl">The tenant's server address, or null/blank for Tableau Public.</param>
    /// <param name="siteContentUrl">The tenant's site (blank for the Default site).</param>
    public static TableauView Parse(string? text, string? serverUrl, string? siteContentUrl)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) throw new RuleException("Enter the web address of the Tableau view.");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new RuleException("The view's web address must be a full address starting with https://.");
        if (url.UserInfo.Length > 0) throw new RuleException("The view's web address can't contain a user name or password.");

        return string.IsNullOrWhiteSpace(serverUrl) ? ParsePublic(url) : ParseServer(url, serverUrl, siteContentUrl);
    }

    private static TableauView ParsePublic(Uri url)
    {
        if (!url.Host.Equals(PublicHost, StringComparison.OrdinalIgnoreCase) || !url.IsDefaultPort)
            throw new RuleException($"With no Tableau Server chosen, the view has to be on Tableau Public ({PublicHost}). To use your own Tableau Server, choose its tenant.");
        var s = Segments(url.AbsolutePath);
        string workbook, view;
        if (s.Count == 3 && Is(s[0], "views")) (workbook, view) = (s[1], s[2]);
        else if (s.Count == 6 && Is(s[0], "app") && Is(s[1], "profile") && Is(s[3], "viz")) (workbook, view) = (s[4], s[5]);
        else throw new RuleException("That isn't a Tableau Public view address. On Tableau Public choose Share, then copy the address from the Embed Code, which looks like https://public.tableau.com/views/WorkbookName/ViewName.");
        Check(workbook, view);
        return Done(new TableauView(true, $"https://{PublicHost}/views/{workbook}/{view}", PublicHost, workbook, view, null));
    }

    private static TableauView ParseServer(Uri url, string serverUrl, string? siteContentUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var server))
            throw new RuleException("The Tableau tenant's server address isn't valid. Fix it in Tenants.");
        if (!url.Authority.Equals(server.Authority, StringComparison.OrdinalIgnoreCase))
            throw new RuleException($"The view has to be on the tenant's Tableau Server ({server.Authority}), not {url.Authority}.");

        // The browser form keeps the path after a "#": https://server/#/site/sales/views/Workbook/View
        var path = url.Fragment.StartsWith("#/", StringComparison.Ordinal) ? url.Fragment[1..].Split('?')[0] : url.AbsolutePath;
        var s = Segments(path);
        string site = "", workbook, view;
        if (s.Count == 3 && Is(s[0], "views")) (workbook, view) = (s[1], s[2]);
        else if (s.Count == 5 && (Is(s[0], "t") || Is(s[0], "site")) && Is(s[2], "views")) (site, workbook, view) = (s[1], s[3], s[4]);
        else throw new RuleException("That isn't a Tableau view address. Open the view in Tableau and copy the address from the browser, which looks like https://your-server/#/views/WorkbookName/ViewName.");
        Check(workbook, view);
        if (site.Length > 0 && !Segment.IsMatch(site)) throw new RuleException("The site name in the address has characters that aren't allowed.");

        var tenantSite = (siteContentUrl ?? "").Trim();
        if (!site.Equals(tenantSite, StringComparison.OrdinalIgnoreCase))
            throw new RuleException(tenantSite.Length == 0
                ? $"The view is on the site “{site}”, but this tenant is for the Default site. Choose the tenant for that site."
                : site.Length == 0 ? $"The address has no site, but this tenant is for the site “{tenantSite}”. Use the view's address from that site."
                : $"The view is on the site “{site}”, but this tenant is for the site “{tenantSite}”. Choose the tenant for that site.");

        var origin = server.GetLeftPart(UriPartial.Authority);
        var src = $"{origin}{(tenantSite.Length > 0 ? "/t/" + tenantSite : "")}/views/{workbook}/{view}";
        return Done(new TableauView(false, src, server.Host, workbook, view, tenantSite.Length > 0 ? tenantSite : null));
    }

    /// <summary>The Embedding API script for a view: Tableau Public's, or the Tableau Server's own.</summary>
    public static string ScriptUrl(TableauView view, string? serverUrl) =>
        view.IsPublic ? $"https://{PublicHost}/javascripts/api/tableau.embedding.3.latest.min.js" : $"{serverUrl?.Trim().TrimEnd('/')}/javascripts/api/tableau.embedding.3.latest.min.js";

    private static List<string> Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
    private static bool Is(string segment, string word) => segment.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static void Check(string workbook, string view)
    {
        if (!Segment.IsMatch(workbook) || !Segment.IsMatch(view) || Odd(workbook) || Odd(view))
            throw new RuleException("The workbook or view name in the address has characters that aren't allowed. Use the address Tableau shows, where spaces appear as %20.");
    }

    /// <summary>A name that decodes to markup, quotes, slashes or control characters. Tableau names have none of these.</summary>
    private static bool Odd(string segment) => Uri.UnescapeDataString(segment).Any(c => c is '<' or '>' or '"' or '\'' or '\\' or '/' or '`' || char.IsControl(c));

    private static TableauView Done(TableauView v) =>
        v.Src.Length > MaxLength ? throw new RuleException($"The view's address is too long (over {MaxLength} characters).") : v;
}

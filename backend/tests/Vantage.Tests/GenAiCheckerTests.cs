using System.Text;
using Vantage.Domain;
using Vantage.Infrastructure.GenAi;

namespace Vantage.Tests;

/// <summary>The GenAI file checks. They need no database.</summary>
public class GenAiCheckerTests
{
    private static readonly string[] Cdns = ["cdnjs.cloudflare.com", "cdn.jsdelivr.net"];

    /// <summary>A minimal page that carries the template marker.</summary>
    private static string Page(string head = "", string body = "<p>Hi</p>") =>
        $"""<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="vantage-template" content="genai-1">{head}</head><body>{body}</body></html>""";

    private static GenAiCheckResult Check(string html, params string[] hosts) => GenAiChecker.Check(Encoding.UTF8.GetBytes(html), hosts.Length == 0 ? Cdns : hosts);

    [Fact]
    public void The_starter_template_passes_its_own_checks_with_the_default_cdns()
    {
        var r = GenAiChecker.Check(GenAiTemplate.Read(), Cdns);

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(["cdn.jsdelivr.net"], r.Libraries);
    }

    [Fact]
    public void A_file_without_the_template_marker_is_refused()
    {
        var r = Check("<html><body>plain</body></html>");

        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains("approved template"));
    }

    [Fact]
    public void The_marker_may_use_either_attribute_order_and_any_case()
    {
        Assert.True(Check("""<HTML><META CONTENT='genai-1' NAME='vantage-template'></HTML>""").Passed);
        Assert.False(Check("""<html><meta name="vantage-template" content="genai-9"></html>""").Passed);
    }

    [Fact]
    public void Libraries_from_approved_cdns_over_https_pass()
    {
        var r = Check(Page("""<script src="https://cdnjs.cloudflare.com/ajax/libs/d3/7.9.0/d3.min.js"></script><link rel="stylesheet" href="//cdn.jsdelivr.net/npm/x@1/x.css">"""));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
        Assert.Equal(["cdn.jsdelivr.net", "cdnjs.cloudflare.com"], r.Libraries);
    }

    [Theory]
    [InlineData("""<script src="https://evil.example.com/x.js"></script>""", "evil.example.com isn't an approved CDN")]
    [InlineData("""<script src="http://cdn.jsdelivr.net/x.js"></script>""", "isn't HTTPS")]
    [InlineData("""<script src="app.js"></script>""", "another file")]
    [InlineData("""<img src="logo.png">""", "another file")]
    [InlineData("""<link rel="stylesheet" href="styles.css">""", "another file")]
    [InlineData("""<style>@import url("https://fonts.googleapis.com/css?family=X");</style>""", "fonts.googleapis.com isn't an approved CDN")]
    [InlineData("""<style>body{background:url(https://tracker.example.net/p.png)}</style>""", "tracker.example.net isn't an approved CDN")]
    [InlineData("""<img srcset="a.png 1x, https://evil.example.com/b.png 2x">""", "another file")]
    public void Anything_loaded_from_elsewhere_is_refused(string head, string expected)
    {
        var r = Check(Page(head));

        Assert.False(r.Passed);
        Assert.Contains(r.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void A_switched_off_cdn_is_not_approved()
    {
        var r = Check(Page("""<script src="https://cdn.jsdelivr.net/x.js"></script>"""), "cdnjs.cloudflare.com");

        Assert.False(r.Passed);
    }

    [Fact]
    public void Inline_data_urls_links_to_other_sites_and_in_page_anchors_are_fine()
    {
        var r = Check(Page("""<style>.a{background:url(data:image/png;base64,AAAA)}</style>""", """<a href="https://example.com/report">more</a><a href="#top">top</a><img src="data:image/svg+xml;utf8,<svg/>">"""));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
    }

    [Theory]
    [InlineData("<iframe src=\"data:text/html,x\"></iframe>", "iframe")]
    [InlineData("<object data=\"x\"></object>", "object")]
    [InlineData("<embed src=\"x\">", "embed")]
    [InlineData("<base href=\"https://evil.example.com/\">", "base")]
    public void Frames_plugins_and_base_tags_are_refused(string body, string tag)
    {
        var r = Check(Page(body: body));

        Assert.Contains(r.Errors, e => e.StartsWith($"<{tag}>"));
    }

    [Fact]
    public void A_meta_refresh_redirect_is_refused()
    {
        Assert.False(Check(Page("""<meta http-equiv="refresh" content="0;url=https://example.com">""")).Passed);
    }

    [Fact]
    public void Network_calls_only_warn_because_the_policy_blocks_them_anyway()
    {
        var r = Check(Page(body: "<script>fetch('/x').then(r => r.json())</script>"));

        Assert.True(r.Passed);
        Assert.Contains(r.Warnings, w => w.Contains("blocked"));
    }

    [Fact]
    public void Size_warns_at_2_MB_and_refuses_over_5_MB()
    {
        string Sized(int bytes) => Page(body: $"<!--{new string('x', bytes)}-->");

        var warn = Check(Sized(Rules.GenAiWarnBytes));
        Assert.True(warn.Passed);
        Assert.Single(warn.Warnings);

        var tooBig = Check(Sized(Rules.GenAiMaxBytes));
        Assert.False(tooBig.Passed);
        Assert.Contains(tooBig.Errors, e => e.Contains("limit"));

        Assert.Empty(Check(Sized(1000)).Warnings);
    }

    [Fact]
    public void Empty_and_non_utf8_files_are_refused()
    {
        Assert.False(GenAiChecker.Check([], Cdns).Passed);
        var r = GenAiChecker.Check([0xFF, 0xFE, 0xFD, 0x80], Cdns);
        Assert.Contains(r.Errors, e => e.Contains("UTF-8"));
    }

    [Fact]
    public void The_policy_allows_only_the_approved_cdns_blocks_network_calls_and_sandboxes_the_page()
    {
        var csp = Vantage.Infrastructure.GenAi.GenAiService.PolicyFor(Cdns, "http://localhost:8080 http://localhost:8081");

        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net", csp);
        Assert.Contains("connect-src 'none'", csp);
        Assert.Contains("frame-ancestors http://localhost:8080 http://localhost:8081", csp);
        Assert.EndsWith("sandbox allow-scripts", csp);
        Assert.DoesNotContain("allow-same-origin", csp);
        Assert.Contains("frame-ancestors 'none'", Vantage.Infrastructure.GenAi.GenAiService.PolicyFor(Cdns, ""));
    }
}

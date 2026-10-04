using System.Text;
using Vantage.Domain;
using Vantage.Infrastructure.GenAi;

namespace Vantage.Tests;

/// <summary>The GenAI file checks. They need no database.</summary>
public class GenAiCheckerTests
{
    private static readonly string[] Cdns = ["cdnjs.cloudflare.com", "cdn.jsdelivr.net"];
    private const string Sri = "sha384-zYPBGXwO4633CABX/5Spf6emCKUJCfoOkhOMYyxMsatqQZPnDblmmOewfjsIVWCM";
    private const string GoodScript = $"""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" integrity="{Sri}" crossorigin="anonymous"></script>""";

    /// <summary>A minimal page that carries the template marker.</summary>
    private static string Page(string head = "", string body = "<p>Hi</p>") =>
        $"""<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="vantage-template" content="genai-1">{head}</head><body>{body}</body></html>""";

    private static GenAiCheckResult Check(string html, params string[] hosts) => GenAiChecker.Check(Encoding.UTF8.GetBytes(html), hosts.Length == 0 ? Cdns : hosts);

    private static void AssertRefused(string html, string expected)
    {
        var r = Check(html);
        Assert.False(r.Passed, "Expected the file to be refused.");
        Assert.Contains(r.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_starter_template_passes_its_own_checks_with_the_default_cdns()
    {
        var r = GenAiChecker.Check(GenAiTemplate.Read(), Cdns);

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Equal(["chart.js@4.4.7 (cdn.jsdelivr.net)"], r.Libraries);
    }

    // ------------------------------------------------------------ template marker

    [Fact]
    public void A_file_without_the_template_marker_is_refused() => AssertRefused("<html><body>plain</body></html>", "approved template");

    [Fact]
    public void The_marker_may_use_either_attribute_order_and_any_case()
    {
        Assert.True(Check("""<HTML><META CONTENT='genai-1' NAME='vantage-template'></HTML>""").Passed);
        Assert.False(Check("""<html><meta name="vantage-template" content="genai-9"></html>""").Passed);
    }

    [Fact]
    public void A_marker_hidden_in_a_comment_does_not_count() =>
        AssertRefused("""<html><!-- <meta name="vantage-template" content="genai-1"> --><body>x</body></html>""", "approved template");

    // ------------------------------------------------------------ libraries

    [Fact]
    public void Pinned_libraries_with_integrity_from_approved_cdns_pass_and_are_listed()
    {
        var r = Check(Page(GoodScript + $"""<script src="https://cdnjs.cloudflare.com/ajax/libs/d3/7.9.0/d3.min.js" integrity="{Sri}" crossorigin></script><link rel="stylesheet" href="//cdn.jsdelivr.net/npm/@scope/x@1.2.3/x.css" integrity="{Sri}" crossorigin="anonymous">"""));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
        Assert.Equal(["@scope/x@1.2.3 (cdn.jsdelivr.net)", "chart.js@4.4.7 (cdn.jsdelivr.net)", "d3@7.9.0 (cdnjs.cloudflare.com)"], r.Libraries);
    }

    [Theory]
    [InlineData("""<script src="https://evil.example.com/x.js" integrity="{SRI}" crossorigin="anonymous"></script>""", "evil.example.com isn't an approved CDN")]
    [InlineData("""<script src="http://cdn.jsdelivr.net/npm/a@1.0.0/a.js" integrity="{SRI}" crossorigin="anonymous"></script>""", "isn't HTTPS")]
    [InlineData("""<script src="app.js"></script>""", "another file")]
    [InlineData("""<img src="logo.png">""", "another file")]
    [InlineData("""<link rel="stylesheet" href="styles.css">""", "another file")]
    [InlineData("""<style>body{background:url(https://tracker.example.net/p.png)}</style>""", "tracker.example.net isn't an approved CDN")]
    [InlineData("""<img srcset="a.png 1x, https://evil.example.com/b.png 2x">""", "another file")]
    [InlineData("""<img alt=">" src="https://evil.example.com/p.png">""", "evil.example.com isn't an approved CDN")] // a ">" in an attribute can't hide the next one
    public void Anything_loaded_from_elsewhere_is_refused(string head, string expected) => AssertRefused(Page(head.Replace("{SRI}", Sri)), expected);

    [Fact]
    public void A_switched_off_cdn_is_not_approved() =>
        Assert.False(Check(Page(GoodScript), "cdnjs.cloudflare.com").Passed);

    [Theory]
    [InlineData("https://cdn.jsdelivr.net/npm/chart.js/dist/chart.umd.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/chart.js@latest/dist/chart.umd.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.js")]
    [InlineData("https://cdn.jsdelivr.net/npm/chart.js@^4.4.0/dist/chart.umd.js")]
    [InlineData("https://cdn.jsdelivr.net/combine/npm/a@1.0.0,npm/b@1.0.0")]
    [InlineData("https://cdnjs.cloudflare.com/ajax/libs/d3/latest/d3.min.js")]
    public void Libraries_must_name_an_exact_version(string url) =>
        AssertRefused(Page($"""<script src="{url}" integrity="{Sri}" crossorigin="anonymous"></script>"""), "exact version");

    [Theory]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" crossorigin="anonymous"></script>""", "integrity")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" integrity="sha384-short" crossorigin="anonymous"></script>""", "integrity")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" integrity="md5-AAAA" crossorigin="anonymous"></script>""", "integrity")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" integrity="{SRI}"></script>""", "crossorigin")]
    [InlineData("""<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.js" integrity="{SRI}" crossorigin="use-credentials"></script>""", "crossorigin")]
    [InlineData("""<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/x@1.0.0/x.css" crossorigin="anonymous">""", "integrity")]
    public void Scripts_and_stylesheets_need_an_integrity_hash_and_crossorigin(string head, string expected) =>
        AssertRefused(Page(head.Replace("{SRI}", Sri)), expected);

    [Fact]
    public void Images_from_approved_cdns_need_no_integrity_hash()
    {
        var r = Check(Page(body: """<img src="https://cdn.jsdelivr.net/npm/some-icons@1.0.0/a.svg"><style>.a{background:url(//cdnjs.cloudflare.com/x.png)}</style>"""));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
    }

    [Fact]
    public void Inline_data_urls_and_in_page_anchors_are_fine()
    {
        var r = Check(Page("""<style>.a{background:url(data:image/png;base64,AAAA)}</style>""", """<a href="#top">top</a><a href="mailto:a@rrd.com">mail</a><img src="data:image/svg+xml;utf8,<svg/>">"""));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
    }

    // ------------------------------------------------------------ structure

    [Theory]
    [InlineData("<iframe src=\"data:text/html,x\"></iframe>", "iframe")]
    [InlineData("<object data=\"x\"></object>", "object")]
    [InlineData("<embed src=\"x\">", "embed")]
    [InlineData("<base href=\"https://evil.example.com/\">", "base")]
    public void Frames_plugins_and_base_tags_are_refused(string body, string tag) =>
        Assert.Contains(Check(Page(body: body)).Errors, e => e.StartsWith($"<{tag}>"));

    [Fact]
    public void A_meta_refresh_redirect_is_refused() => AssertRefused(Page("""<meta http-equiv="refresh" content="0;url=https://example.com">"""), "redirect");

    [Theory]
    [InlineData("""<link rel="prefetch" href="https://cdn.jsdelivr.net/npm/a@1.0.0/a.js">""")]
    [InlineData("""<link rel="dns-prefetch" href="//cdn.jsdelivr.net">""")]
    [InlineData("""<link rel="preconnect" href="https://cdn.jsdelivr.net">""")]
    [InlineData("""<link rel="manifest" href="m.json">""")]
    public void Links_that_are_not_stylesheets_are_refused(string head) => AssertRefused(Page(head), "isn't allowed");

    [Theory]
    [InlineData("""<script type="importmap">{"imports":{"x":"https://evil.example.com/x.js"}}</script>""")]
    [InlineData("""<script type="speculationrules">{"prefetch":[]}</script>""")]
    public void Import_maps_and_speculation_rules_are_refused(string body) => AssertRefused(Page(body: body), "isn't allowed");

    [Fact]
    public void A_json_data_block_is_fine() => Assert.True(Check(Page(body: """<script type="application/json" id="d">{"a":1}</script>""")).Passed);

    [Theory]
    [InlineData("""<a href="https://example.com/report">more</a>""")]
    [InlineData("""<a href="//example.com/report">more</a>""")]
    public void Links_to_other_sites_are_refused(string body) => AssertRefused(Page(body: body), "other sites");

    // ------------------------------------------------------------ risky code

    [Theory]
    [InlineData("eval('1+1')", "eval()")]
    [InlineData("var f = new Function('return 1')", "new Function()")]
    [InlineData("var f = Function('return 1')", "new Function()")]
    [InlineData("setTimeout('doIt()', 10)", "setTimeout")]
    [InlineData("document.write('<b>x</b>')", "document.write()")]
    [InlineData("var c = document.cookie", "document.cookie")]
    [InlineData("window.open('https://example.com')", "window.open()")]
    [InlineData("window.top.location = 'x'", "window.top")]
    [InlineData("parent.postMessage('x', '*')", "window.top")]
    [InlineData("location.href = 'https://example.com'", "Navigating")]
    [InlineData("window.location.assign('https://example.com')", "Navigating")]
    [InlineData("location = 'https://example.com'", "Navigating")]
    [InlineData("var w = new Worker('x.js')", "Workers")]
    [InlineData("navigator.serviceWorker.register('x')", "Workers")]
    [InlineData("WebAssembly.instantiate(b)", "WebAssembly")]
    [InlineData("import('https://cdn.jsdelivr.net/npm/a@1.0.0/a.js')", "import()")]
    [InlineData("x.postMessage('hi')", "postMessage()")]
    [InlineData("navigator.sendBeacon('/x', d)", "sendBeacon")]
    [InlineData("fetch('/x')", "Network calls")]
    [InlineData("new XMLHttpRequest()", "Network calls")]
    [InlineData("new WebSocket('wss://x')", "Network calls")]
    public void Risky_code_is_refused(string code, string expected) => AssertRefused(Page(body: $"<script>{code}</script>"), expected);

    [Fact]
    public void A_javascript_url_is_refused() => AssertRefused(Page(body: """<b onmouseover="javascript:alert(1)">b</b>"""), "javascript:");

    [Fact]
    public void Ordinary_chart_code_is_not_mistaken_for_risky_code()
    {
        var r = Check(Page(body: """
            <script>
              const total = DATA.rows.reduce((a, r) => a + r.value, 0);
              document.getElementById('t').textContent = total.toLocaleString('en-US');
              document.title = 'Sales'; window.addEventListener('resize', () => chart.resize());
              const url = new URL('#x', 'https://example.com'); const g = globalThis.Chart; const f = function () { return 1 };
              setTimeout(() => draw(), 10); node.parent.children.forEach(c => c.remove()); const lc = locationName === 'x';
            </script>
            """));

        Assert.True(r.Passed, string.Join(" | ", r.Errors));
    }

    [Fact]
    public void Long_encoded_blobs_and_atob_only_warn()
    {
        var blob = new string('A', 5000);
        var r = Check(Page(body: $"<script>const d = '{blob}'; const t = atob(d);</script><img src=\"data:image/png;base64,{blob}\">"));

        Assert.True(r.Passed);
        Assert.Equal(2, r.Warnings.Count);

        var dataUrlOnly = Check(Page(body: $"<img src=\"data:image/png;base64,{blob}\">"));
        Assert.Empty(dataUrlOnly.Warnings);
    }

    // ------------------------------------------------------------ size and encoding

    [Fact]
    public void Size_warns_at_2_MB_and_refuses_over_5_MB()
    {
        string Sized(int bytes) => Page(body: $"<!--{new string('-', bytes)}-->");

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

    // ------------------------------------------------------------ serving policy

    [Fact]
    public void The_policy_allows_only_the_approved_cdns_blocks_network_calls_and_sandboxes_the_page()
    {
        var csp = GenAiService.PolicyFor(Cdns, "http://localhost:8080 http://localhost:8081");

        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'unsafe-inline' https://cdnjs.cloudflare.com https://cdn.jsdelivr.net", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("connect-src 'none'", csp);
        Assert.Contains("frame-ancestors http://localhost:8080 http://localhost:8081", csp);
        Assert.EndsWith("sandbox allow-scripts", csp);
        Assert.DoesNotContain("allow-same-origin", csp);
        Assert.Contains("frame-ancestors 'none'", GenAiService.PolicyFor(Cdns, ""));
    }
}

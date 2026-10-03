using System.Text.RegularExpressions;

namespace Vantage.Domain;

/// <summary>Field limits and naming rules agreed in the requirements (v3, Oct 2026).</summary>
public static partial class Rules
{
    public const int GroupNameMax = 40;
    public const int DashboardCodeMax = 20;
    public const int DashboardNameMax = 150;
    public const int DescriptionMax = 500;
    public const int TagMax = 10;
    public const int TagsPerDashboardMax = 8;
    public const int PinsPerUserMax = 12;
    public const int VersionsKept = 3;
    public const int ThumbnailWidth = 640;
    public const int ThumbnailHeight = 360;
    public const int ThumbnailMaxBytes = 1 * 1024 * 1024;
    public const int GenAiMaxBytes = 5 * 1024 * 1024;
    public const int GenAiWarnBytes = 2 * 1024 * 1024;
    public const int CategoryNameMax = 100;
    public const int CategoryLevels = 3;
    public const int SharePointPathMax = 400;
    public const int RlsValueMax = 100;

    /// <summary>Tags as entered (comma, semicolon or space separated): trimmed, de-duplicated ignoring case, empty ones dropped.</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? []).SelectMany(t => (t ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .DistinctBy(t => t.ToUpperInvariant()).ToList();

    /// <summary>Default group name: &lt;Dashboard code&gt;-&lt;Dashboard ID&gt;-default. Always within 40 characters.</summary>
    public static string DefaultGroupName(string dashboardCode, int dashboardId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dashboardId);
        var code = dashboardCode.Trim();
        if (code.Length == 0) throw new ArgumentException("Dashboard code is required.", nameof(dashboardCode));
        var suffix = $"-{dashboardId}-default";
        var room = GroupNameMax - suffix.Length;
        if (code.Length > room) code = code[..room];
        return code + suffix;
    }

    /// <summary>Dashboard codes: letters, digits, hyphen and underscore, 1 to 20 characters.</summary>
    public static bool IsValidDashboardCode(string? code) =>
        code is { Length: > 0 and <= DashboardCodeMax } && CodePattern().IsMatch(code);

    /// <summary>Group names: letters, digits, hyphen and underscore, 1 to 40 characters.</summary>
    public static bool IsValidGroupName(string? name) =>
        name is { Length: > 0 and <= GroupNameMax } && CodePattern().IsMatch(name);

    /// <summary>Tags: alphanumeric only, 1 to 10 characters.</summary>
    public static bool IsValidTag(string? tag) =>
        tag is { Length: > 0 and <= TagMax } && TagPattern().IsMatch(tag);

    public static bool IsInternalEmail(string email, string internalDomain = "rrd.com") =>
        email.Trim().EndsWith("@" + internalDomain, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex TagPattern();
}

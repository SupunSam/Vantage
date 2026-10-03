namespace Vantage.Infrastructure.Embedding;

/// <summary>What the browser needs to render a dashboard. Tokens are short-lived and never stored.</summary>
public sealed record EmbedInfo(
    string Type,
    int DashboardId,
    string Name,
    string? EmbedUrl,
    string? Token,
    DateTimeOffset? ExpiresAt,
    string? ReportId,
    string? TableauScriptUrl);

public sealed class EmbedException(string code, string message) : Exception(message)
{
    /// <summary>Stable code the UI maps to a friendly message: not-linked, not-configured, secret-missing, platform-error.</summary>
    public string Code { get; } = code;
}

/// <summary>Effective identity for Power BI RLS, sent in the GenerateToken call.</summary>
public sealed record RlsIdentity(string Username, IReadOnlyList<string> Roles, IReadOnlyList<string> Datasets);

public static class RlsIdentityBuilder
{
    /// <summary>
    /// Builds the identity list for GenerateToken from what the semantic model actually requires (read from Power BI),
    /// so the RLS checkbox on the publish form can't cause a mismatch:
    /// no RLS in the model → no identity (Power BI rejects one); RLS roles in the model → the user's email plus the
    /// group's role, and a group without a role is refused with a clear message.
    /// </summary>
    public static IReadOnlyList<RlsIdentity> Build(bool identityRequired, bool rolesRequired, string userEmail, string? groupRlsValue, Guid datasetId)
    {
        if (!identityRequired) return [];
        var roles = groupRlsValue?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (rolesRequired && roles.Length == 0)
            throw new EmbedException("rls-missing", "This report uses row-level security, but your group has no RLS role. Ask a Super Admin to set the group's RLS role to one of the roles defined in the report.");
        return [new RlsIdentity(userEmail, roles, [datasetId.ToString()])];
    }
}

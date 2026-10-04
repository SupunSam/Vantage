using System.Text.Json;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// Turns an audit row into one plain sentence for the Audit Log ("Added 18 people to group SALES-North on Sales Dashboard").
/// Built when the log is read, so older entries get descriptions too. Anything unknown falls back to the action code.
/// </summary>
public static class AuditDescriber
{
    /// <param name="subject">Name of the thing the row is about (user email, group, role, category, tenant), if it still exists.</param>
    /// <param name="dashboard">Name of the dashboard the row belongs to, if any.</param>
    public static string Describe(string action, string entityType, string? entityId, string? details, string? subject, string? dashboard, string? serviceNow)
    {
        JsonElement? d = null;
        if (!string.IsNullOrWhiteSpace(details))
        {
            try { var e = JsonDocument.Parse(details).RootElement; if (e.ValueKind == JsonValueKind.Object) d = e; } catch (JsonException) { }
        }

        string? Str(string name) => d is { } e && e.TryGetProperty(name, out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()) : null;
        bool Flag(string name) => d is { } e && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
        int Count(string name) => d is { } e && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        string? Join(string name) => d is { } e && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? string.Join(", ", p.EnumerateArray().Select(x => x.ToString())) : null;

        var on = dashboard is null ? "" : $" on {dashboard}";
        var who = subject ?? (entityId is null ? entityType : $"{entityType} #{entityId}");
        var text = action switch
        {
            "user.created" => Flag("bulk") ? $"Added user {Str("Email") ?? who} (bulk upload)." : $"Added user {Str("Email") ?? who} ({Str("type")?.ToLowerInvariant()}).",
            "user.bulk-added" => $"Added {Count("added")} of {Count("total")} users in a bulk upload.",
            "user.updated" => $"Changed roles of {who}: {OrNone(Join("rolesBefore"))} → {OrNone(Join("rolesAfter"))}.",
            "user.status-changed" => $"Changed status of {who} from {Str("from")} to {Str("to")}.",
            "user.email-changed" => $"Changed email from {Str("from")} to {Str("to")}.",
            "user.deactivated-by-hrms" => $"Deactivated {who} after the HR system showed they left.",
            "hrms.sync" => $"Synced users with the HR system: {Count("updated")} updated, {Count("deactivated")} deactivated, {Count("skipped")} skipped.",

            "role.created" => $"Created role {Str("Name") ?? who}.",
            "role.updated" => $"Changed permissions of role {Str("Name") ?? who}.",
            "role.deleted" => $"Deleted role {Str("Name") ?? who}.",

            "tenant.created" => $"Added tenant {Str("Name") ?? who} ({Str("platform")}).",
            "tenant.updated" => $"Updated tenant {Str("Name") ?? who}{(Flag("secretReplaced") ? " and replaced its secret" : "")}.",
            "tenant.verified" => Flag("Passed") || Flag("passed") ? $"Verified tenant {who}: all checks passed." : $"Verified tenant {who}: some checks failed ({Join("failed")}).",

            "category.created" => $"Created category {Str("Name")} at level {Str("level")}.",
            "category.renamed" => $"Renamed category {Str("from")} to {Str("to")}.",
            "category.deleted" => $"Deleted category {Str("Name")}.",

            "dashboard.created" => $"Created dashboard {Str("Name") ?? dashboard} ({Str("Code")}, {Str("type")}) with default group {Str("defaultGroup")}.",
            "dashboard.publish-started" => $"Started publishing {dashboard ?? who} from file {Str("file")} to workspace {Str("workspace")}.",
            "dashboard.publish-retried" => $"Retried publishing {dashboard ?? who} from file {Str("file")}.",
            "dashboard.published" => $"Published {dashboard ?? who}; it is now live.",
            "dashboard.publish-failed" => $"Publishing {dashboard ?? who} failed: {Str("error")}",
            "dashboard.updated" => $"Edited the details of {dashboard ?? who}.",
            "dashboard.thumbnail-set" => $"Set a new thumbnail for {dashboard ?? who} ({Str("Width")}×{Str("Height")}).",
            "dashboard.thumbnail-removed" => $"Removed the thumbnail of {dashboard ?? who}.",
            "dashboard.rls-corrected" => $"Corrected the row-level security flag of {dashboard ?? who} to match the report ({YesNo(Str("to") ?? Str("model"))}).",
            "dashboard.report-id-changed" => $"The Power BI report of {dashboard ?? who} was replaced by a new report.",
            "dashboard.replace-started" => $"Started uploading version {Str("version")} of {dashboard ?? who}.",
            "dashboard.replaced" => $"Replaced the {(Str("type") == "GenAi" ? "page" : "report")} of {dashboard ?? who} with version {Str("version")}.",
            "dashboard.restored" => $"Restored version {Str("fromVersion")} of {dashboard ?? who}; it is live again as version {Str("version")}.",
            "dashboard.replace-failed" => $"Replacing the report of {dashboard ?? who} (version {Str("version")}) failed: {Str("error")}",
            "dashboard.version-downloaded" => $"Downloaded version {Str("version")} of {dashboard ?? who}.",
            "dashboard.version-removed" => $"Removed version {Str("version")} of {dashboard ?? who}. {Str("reason")}.",
            "dashboard.previewed" => $"Previewed {dashboard ?? who} as a member of group {Str("group")}{(Str("rls") is { } pr ? $" (RLS role {pr})" : "")}. Not counted as usage.",
            "embed.token-issued" => $"Opened {dashboard ?? who}{(Str("rls") is { } rls ? $" with row-level security role {rls}" : "")}.",

            "group.created" => $"Created access group {Str("Name") ?? who}{on}{(Str("RlsValue") is { } r ? $" with RLS value {r}" : "")}.",
            "group.renamed" => $"Renamed access group {Str("from")} to {Str("to")}{on}.",
            "group.rls-changed" => $"Changed the RLS value of group {who}{on} from {OrNone(Str("from"))} to {OrNone(Str("to"))}.",
            "group.activated" => $"Activated access group {Str("group") ?? who}{on}.",
            "group.deactivated" => $"Deactivated access group {Str("group") ?? who}{on}.",
            "group.members-added" => $"Added {Count("count")} {People(Count("count"))} to group {Str("group") ?? who}{on}{(Count("moved") > 0 ? $" ({Count("moved")} moved from another group)" : "")}: {Brief(Join("emails"))}{(Str("overrideReason") is { } why ? $". Added by a Super Admin without the owners' approval, reason: {why}" : Count("requestId") > 0 ? ", after the owners approved" : "")}.",
            "group.add-requested" => $"{(Str("rule") is { } ra ? $"Access rule “{ra}” asked" : "Asked")} the owners to approve adding {Count("count")} {People(Count("count"))} to group {Str("group") ?? who}{on}: {Brief(Join("emails"))}.",
            "group.add-approved" => $"{(Str("overrideReason") is not null ? "A Super Admin approved in place of the owners" : "The owners approved")} adding {Count("approved")} of {Count("total")} {People(Count("total"))} to group {Str("group") ?? who}{on}{(Count("rejected") > 0 ? $", rejected {Count("rejected")}" : "")}{(Str("overrideReason") is { } r2 ? $". Reason: {r2}" : "")}.",
            "group.add-rejected" => $"{(Str("overrideReason") is not null ? "A Super Admin rejected in place of the owners" : "The owners rejected")} adding {Count("total")} {People(Count("total"))} to group {Str("group") ?? who}{on}{(Str("overrideReason") is { } r3 ? $". Reason: {r3}" : "")}.",
            "group.remove-requested" => $"{(Str("rule") is { } rq ? $"Access rule “{rq}” asked" : "Asked")} the owners to approve removing {Count("count")} {People(Count("count"))} from group {Str("group") ?? who}{on}: {Brief(Join("emails"))}.",
            "group.remove-approved" => $"{(Str("overrideReason") is not null ? "A Super Admin approved in place of the owners" : "The owners approved")} removing {Count("approved")} of {Count("total")} {People(Count("total"))} from group {Str("group") ?? who}{on}{(Count("rejected") > 0 ? $", kept {Count("rejected")}" : "")}{(Str("overrideReason") is { } r4 ? $". Reason: {r4}" : "")}.",
            "group.remove-rejected" => $"{(Str("overrideReason") is not null ? "A Super Admin rejected in place of the owners" : "The owners rejected")} removing {Count("total")} {People(Count("total"))} from group {Str("group") ?? who}{on}{(Str("overrideReason") is { } r5 ? $". Reason: {r5}" : "")}.",
            "group.rule-created" => $"Created the {Str("action")?.ToLowerInvariant()} rule “{Str("rule")}” for group {who}{on}: {Str("when")}.",
            "group.rule-updated" => $"Changed the {Str("action")?.ToLowerInvariant()} rule “{Str("rule")}” for group {who}{on}: {Str("when")}.",
            "group.rule-deleted" => $"Deleted the rule “{Str("rule")}” of group {who}{on}.",
            "group.rule-enabled" => $"Switched on the rule “{Str("rule")}” of group {who}{on}.",
            "group.rule-disabled" => $"Switched off the rule “{Str("rule")}” of group {who}{on}.",
            "group.rule-run" => $"The rule “{Str("rule")}” sent {Count("count")} {People(Count("count"))} to the owners to {(Str("action") == "Remove" ? "remove" : "add")} for group {who}{on}.",
            "config.setting-changed" => $"Changed the setting “{Str("label")}” from {Str("from")} to {Str("to")}.",
            "config.logo-changed" => $"Uploaded a new portal logo ({Str("file")}).",
            "config.logo-reset" => "Went back to the standard portal logo.",
            "config.cdn-added" => $"Added {Str("host")} to the approved CDNs.",
            "config.cdn-updated" => Flag("activeChanged") ? $"{(Flag("active") ? "Switched on" : "Switched off")} the approved CDN {Str("host")}." : $"Updated the notes of the approved CDN {Str("host")}.",
            "config.cdn-removed" => $"Removed {Str("host")} from the approved CDNs.",
            "config.service-type-updated" => $"Updated {Str("type")} dashboards: {(Flag("enabled") ? "on" : "off")}{(Str("maxFileSizeMb") is { } mb ? $", largest upload {mb} MB" : "")}.",
            "group.add-cancelled" => $"Withdrew the request to add people to group {Str("group") ?? who}{on}.",
            "group.member-removed" => $"Removed {Str("email")} from group {Str("group") ?? who}{on}.",
            "group.member-moved" => $"Moved {Str("email")} from group {Str("from")} to {Str("to")}{on}.",
            "group.members-copied" => $"Copied {Count("copied")} {People(Count("copied"))} into group {who}.",
            "group.self-joined" => $"A Super Admin added themselves to group {Str("group") ?? who}{on}.",

            "access-request.created" => $"{Str("requester")} asked for access to {dashboard ?? who}{(Str("comment") is { } c ? $": “{c}”" : "")}.",
            "access-request.cancelled" => $"Access request for {dashboard ?? who} was withdrawn.",
            "access-request.approved" => $"Approved {Str("requester")} for {dashboard ?? who} in group {Str("group")}{(Str("overrideReason") is { } ar ? $". A Super Admin decided in place of the owners, reason: {ar}" : Flag("onBehalf") ? ", decided for the owners" : "")}.",
            "access-request.rejected" => $"Rejected the request from {Str("requester")} for {dashboard ?? who}{(Str("overrideReason") is { } rr ? $". A Super Admin decided in place of the owners, reason: {rr}" : Flag("onBehalf") ? ", decided for the owners" : "")}.",
            _ => Fallback(action, who, dashboard),
        };
        return string.IsNullOrWhiteSpace(serviceNow) ? text : $"{text} Ticket {serviceNow}.";
    }

    private static string OrNone(string? v) => string.IsNullOrWhiteSpace(v) ? "none" : v;
    private static string YesNo(string? v) => v is null ? "unknown" : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ? "RLS on" : "RLS off";
    private static string People(int n) => n == 1 ? "person" : "people";
    private static string Brief(string? emails)
    {
        if (string.IsNullOrEmpty(emails)) return "no emails recorded";
        var parts = emails.Split(", ");
        return parts.Length <= 3 ? emails : $"{string.Join(", ", parts.Take(3))} and others";
    }

    private static string Fallback(string action, string who, string? dashboard)
    {
        var verb = action[(action.IndexOf('.') + 1)..].Replace('-', ' ');
        verb = char.ToUpperInvariant(verb[0]) + verb[1..];
        return $"{verb}: {dashboard ?? who}.";
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Secrets;

namespace Vantage.Infrastructure.Tenants;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CheckResult>))]
public enum CheckResult { Pass, Warn, Fail }

public sealed record VerifyCheck(string Name, CheckResult Result, string Detail);

public sealed record VerifyReport(int TenantId, bool Passed, DateTime VerifiedAtUtc, IReadOnlyList<VerifyCheck> Checks);

/// <summary>
/// "Verify connection" in the Tenant Master. For Power BI it checks, in order, everything publishing and
/// licence-free embedding depend on: service principal sign-in, Power BI API access, the service principal's
/// role on each workspace (Contributor or higher to publish), and that each workspace is on a dedicated
/// (Fabric F64) capacity. Read-only admin API access (for usage analytics) is reported as a warning only.
/// For Tableau it signs in to the REST API with a Connected App JWT.
/// </summary>
public sealed class TenantVerifier(HttpClient http, AppDbContext db, IPowerBiTokenProvider tokens, ISecretStore secrets,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] PublishRoles = ["Admin", "Member", "Contributor"];

    public async Task<VerifyReport> VerifyAsync(int tenantId, CancellationToken ct)
    {
        var tenant = await db.BiTenants.Include(t => t.Workspaces).SingleOrDefaultAsync(t => t.Id == tenantId, ct)
                     ?? throw new KeyNotFoundException();

        var checks = tenant.Platform == BiPlatform.PowerBi
            ? await VerifyPowerBiAsync(tenant, ct)
            : await VerifyTableauAsync(tenant, ct);

        var report = new VerifyReport(tenant.Id, checks.All(c => c.Result != CheckResult.Fail), clock.GetUtcNow().UtcDateTime, checks);
        tenant.LastVerifiedAtUtc = report.VerifiedAtUtc;
        tenant.LastVerifyPassed = report.Passed;
        tenant.LastVerifyReport = JsonSerializer.Serialize(checks, Json);
        await db.SaveChangesAsync(ct);
        return report;
    }

    public static IReadOnlyList<VerifyCheck> ReadReport(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<VerifyCheck>>(json, Json) ?? [];

    // ---------------------------------------------------------------- Power BI

    private async Task<List<VerifyCheck>> VerifyPowerBiAsync(BiTenant tenant, CancellationToken ct)
    {
        var checks = new List<VerifyCheck>();

        // 1. Sign in
        string token;
        try
        {
            token = await tokens.GetAccessTokenAsync(tenant, ct);
            checks.Add(new("Service principal sign-in", CheckResult.Pass, $"Signed in to tenant {tenant.AzureTenantId} as client {tenant.ClientId}."));
        }
        catch (EmbedException ex)
        {
            checks.Add(new("Service principal sign-in", CheckResult.Fail, ex.Message));
            return checks;
        }
        var principalIds = PrincipalIds(token, tenant.ClientId);

        // 2. API access (tenant setting "Service principals can use Fabric APIs")
        var (status, body) = await GetAsync(token, "groups?$top=5000", ct);
        if (status != HttpStatusCode.OK)
        {
            checks.Add(new("Power BI API access", CheckResult.Fail, status == HttpStatusCode.Unauthorized
                ? "Power BI refused the service principal (401). In the Fabric admin portal, enable 'Service principals can use Fabric APIs' for a security group that contains this app."
                : $"Power BI returned {(int)status}: {Trim(body)}"));
            return checks;
        }
        checks.Add(new("Power BI API access", CheckResult.Pass, "The tenant allows this service principal to call Power BI APIs."));
        var groups = JsonSerializer.Deserialize<ListDto<GroupDto>>(body, Json)?.Value ?? [];

        // 3 and 4. Each workspace: membership/role and capacity
        var active = tenant.Workspaces.Where(w => w.IsActive).ToList();
        if (active.Count == 0)
            checks.Add(new("Workspaces", CheckResult.Fail, "Add at least one workspace to publish into."));

        foreach (var ws in active)
        {
            var group = groups.FirstOrDefault(g => g.Id == ws.WorkspaceId);
            if (group is null)
            {
                ws.ServicePrincipalAccess = null;
                ws.OnDedicatedCapacity = null;
                checks.Add(new($"Workspace '{ws.Name}': access", CheckResult.Fail,
                    $"The service principal is not a member of workspace {ws.WorkspaceId}, or the ID is wrong. Add the app to the workspace as Admin, Member or Contributor."));
                continue;
            }

            var role = await GetRoleAsync(token, ws.WorkspaceId, principalIds, ct);
            ws.ServicePrincipalAccess = role ?? "Member (role not readable)";
            if (role is null)
                checks.Add(new($"Workspace '{ws.Name}': access", CheckResult.Warn,
                    "The service principal can see the workspace, but its role could not be read. Publishing needs Admin, Member or Contributor."));
            else if (PublishRoles.Contains(role))
                checks.Add(new($"Workspace '{ws.Name}': access", CheckResult.Pass, $"The service principal is {role} of '{group.Name}', enough to publish and embed."));
            else
                checks.Add(new($"Workspace '{ws.Name}': access", CheckResult.Fail, $"The service principal is only {role}. Publishing needs Admin, Member or Contributor."));

            ws.OnDedicatedCapacity = group.IsOnDedicatedCapacity;
            checks.Add(group.IsOnDedicatedCapacity
                ? new($"Workspace '{ws.Name}': capacity", CheckResult.Pass, $"On dedicated capacity {group.CapacityId}, so viewers don't need Power BI licences.")
                : new($"Workspace '{ws.Name}': capacity", CheckResult.Fail, "Not on a Fabric/Premium capacity. Assign the workspace to the F64 capacity so embedded viewers don't need licences."));
        }

        // 5. Read-only admin APIs (needed later for Power BI usage analytics) – warning only
        var (adminStatus, _) = await GetAsync(token, "admin/groups?$top=1", ct);
        checks.Add(adminStatus == HttpStatusCode.OK
            ? new("Admin APIs for usage analytics", CheckResult.Pass, "Read-only admin APIs are enabled; Power BI view events can be imported.")
            : new("Admin APIs for usage analytics", CheckResult.Warn,
                "Not enabled yet. Analytics will use portal events only until 'Service principals can access read-only admin APIs' is turned on. Not needed for publishing."));

        return checks;
    }

    private async Task<string?> GetRoleAsync(string token, Guid workspaceId, HashSet<string> principalIds, CancellationToken ct)
    {
        var (status, body) = await GetAsync(token, $"groups/{workspaceId}/users", ct);
        if (status != HttpStatusCode.OK) return null;
        var users = JsonSerializer.Deserialize<ListDto<GroupUserDto>>(body, Json)?.Value ?? [];
        var me = users.FirstOrDefault(u => string.Equals(u.PrincipalType, "App", StringComparison.OrdinalIgnoreCase)
                                           && (principalIds.Contains(u.Identifier ?? "") || principalIds.Contains(u.GraphId ?? "")));
        return me?.GroupUserAccessRight;
    }

    /// <summary>The service principal can be listed by its object ID (oid claim) or its client ID.</summary>
    private static HashSet<string> PrincipalIds(string accessToken, string? clientId)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(clientId)) ids.Add(clientId);
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            foreach (var claim in jwt.Claims.Where(c => c.Type is "oid" or "appid"))
                ids.Add(claim.Value);
        }
        catch (ArgumentException)
        {
            // Not a JWT (tests): fall back to the client ID.
        }
        return ids;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string token, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, PowerBiClient.ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await http.SendAsync(req, ct);
            return (res.StatusCode, await res.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (HttpStatusCode.ServiceUnavailable, $"Couldn't reach api.powerbi.com: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Tableau

    private async Task<List<VerifyCheck>> VerifyTableauAsync(BiTenant tenant, CancellationToken ct)
    {
        var checks = new List<VerifyCheck>();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(tenant.ServerUrl)) missing.Add("server URL");
        if (string.IsNullOrWhiteSpace(tenant.ConnectedAppClientId)) missing.Add("Connected App client ID");
        if (string.IsNullOrWhiteSpace(tenant.ConnectedAppSecretId)) missing.Add("secret ID");
        if (string.IsNullOrWhiteSpace(tenant.VerifyUserName)) missing.Add("test user name");
        if (missing.Count > 0)
        {
            checks.Add(new("Configuration", CheckResult.Fail, "Missing " + string.Join(", ", missing) + "."));
            return checks;
        }
        checks.Add(new("Configuration", CheckResult.Pass, "Server, Connected App and test user are filled in."));

        var secret = tenant.SecretName is null ? null : await secrets.GetAsync(tenant.SecretName, ct);
        if (secret is null)
        {
            checks.Add(new("Connected App secret", CheckResult.Fail, "Enter the Connected App secret value."));
            return checks;
        }
        checks.Add(new("Connected App secret", CheckResult.Pass, "Secret value is stored (encrypted)."));

        var (jwt, _) = TableauTokenService.Sign(tenant.ConnectedAppClientId!, tenant.ConnectedAppSecretId!, secret, tenant.VerifyUserName!, clock.GetUtcNow());
        var url = $"{tenant.ServerUrl!.TrimEnd('/')}/api/{tenant.TableauApiVersion}/auth/signin";
        var payload = new { credentials = new { jwt, site = new { contentUrl = tenant.SiteContentUrl ?? "" } } };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            checks.Add(res.IsSuccessStatusCode
                ? new("Connected App sign-in", CheckResult.Pass, $"Signed in to {tenant.ServerUrl} as {tenant.VerifyUserName}.")
                : new("Connected App sign-in", CheckResult.Fail, $"Tableau returned {(int)res.StatusCode}: {Trim(body)}. Check the Connected App is enabled and the user exists and is licensed."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            checks.Add(new("Connected App sign-in", CheckResult.Fail, $"Could not reach {tenant.ServerUrl}: {ex.Message}"));
        }
        return checks;
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] + "…" : body;

    private sealed record ListDto<T>(List<T> Value);
    private sealed record GroupDto(Guid Id, string Name, bool IsOnDedicatedCapacity, Guid? CapacityId);
    private sealed record GroupUserDto(string? Identifier, string? GraphId, string? PrincipalType, string? GroupUserAccessRight, string? DisplayName);
}

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Secrets;

namespace Vantage.Infrastructure.Embedding;

public sealed record PowerBiReport(Guid Id, string Name, string EmbedUrl, Guid DatasetId, string? WebUrl);
public sealed record PowerBiToken(string Token, DateTimeOffset Expiration);

/// <summary>What the semantic model needs in an embed token: an effective identity, and RLS roles with it.</summary>
public sealed record PowerBiDataset(Guid Id, string Name, bool IdentityRequired, bool RolesRequired);

/// <summary>State of a .pbix import. State is Publishing, Succeeded or Failed.</summary>
public sealed record PowerBiImport(Guid Id, string State, Guid? ReportId, Guid? DatasetId, string? Error);

/// <summary>Entra ID access token for the Power BI REST API, acquired as the tenant's service principal.</summary>
public interface IPowerBiTokenProvider
{
    Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct);
}

/// <summary>Client-credentials flow with MSAL; one confidential client per tenant, MSAL caches the token.</summary>
public sealed class MsalPowerBiTokenProvider(ISecretStore secrets, ILogger<MsalPowerBiTokenProvider> log) : IPowerBiTokenProvider
{
    private static readonly string[] Scopes = ["https://analysis.windows.net/powerbi/api/.default"];
    private static readonly ConcurrentDictionary<string, IConfidentialClientApplication> Apps = new();

    public async Task<string> GetAccessTokenAsync(BiTenant tenant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.AzureTenantId) || string.IsNullOrWhiteSpace(tenant.ClientId) || string.IsNullOrWhiteSpace(tenant.SecretName))
            throw new EmbedException("not-configured", $"Power BI tenant '{tenant.Name}' is missing its tenant ID, client ID or secret name.");

        var secret = await secrets.GetAsync(tenant.SecretName, ct)
            ?? throw new EmbedException("secret-missing", $"Client secret '{tenant.SecretName}' is not set. Run setup-secrets.ps1 (or start-local.cmd) again.");

        var cacheKey = $"{tenant.AzureTenantId}|{tenant.ClientId}|{secret.GetHashCode()}";
        var app = Apps.GetOrAdd(cacheKey, _ => ConfidentialClientApplicationBuilder.Create(tenant.ClientId)
            .WithClientSecret(secret)
            .WithAuthority($"https://login.microsoftonline.com/{tenant.AzureTenantId}")
            .Build());

        try
        {
            var result = await app.AcquireTokenForClient(Scopes).ExecuteAsync(ct);
            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            log.LogWarning(ex, "Service principal sign-in failed for tenant {Tenant}", tenant.Name);
            var hint = ex.ErrorCode switch
            {
                "invalid_client" when ex.Message.Contains("AADSTS7000215") => "The client secret is not valid. Make sure you entered the secret's Value, not its Secret ID, and that it hasn't expired.",
                "invalid_client" when ex.Message.Contains("AADSTS7000222") => "The client secret has expired. Create a new one in Entra ID and enter its Value.",
                "invalid_client" => "The client secret is wrong or expired.",
                "unauthorized_client" or "invalid_request" when ex.Message.Contains("AADSTS700016") => "No app with this client ID exists in this tenant.",
                "invalid_request" when ex.Message.Contains("AADSTS90002") => "The Azure tenant ID was not found.",
                _ => "Check the tenant ID, client ID and secret.",
            };
            throw new EmbedException("platform-error", $"Service principal sign-in failed ({ex.ErrorCode}). {hint}");
        }
        catch (Exception ex) when (ex is MsalClientException or HttpRequestException)
        {
            log.LogWarning(ex, "Could not reach Microsoft sign-in for tenant {Tenant}", tenant.Name);
            var root = ex is HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } ||
                       ex.InnerException is HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException };
            throw new EmbedException("network", root
                ? "Couldn't reach Microsoft sign-in: the HTTPS certificate isn't trusted. If your network inspects HTTPS traffic, put your company's root certificate (.crt) in deploy/certs and rebuild."
                : $"Couldn't reach Microsoft sign-in (login.microsoftonline.com): {ex.Message}");
        }
    }
}

/// <summary>Calls the Power BI REST API as the tenant's service principal (app-owns-data).</summary>
public sealed class PowerBiClient(HttpClient http, IPowerBiTokenProvider tokens, ILogger<PowerBiClient> log)
{
    public const string ApiBase = "https://api.powerbi.com/v1.0/myorg/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Signs in as the service principal; throws EmbedException with a clear reason when it can't.</summary>
    public Task EnsureSignInAsync(BiTenant tenant, CancellationToken ct) => tokens.GetAccessTokenAsync(tenant, ct);

    public async Task<PowerBiReport> GetReportAsync(BiTenant tenant, Guid workspaceId, Guid reportId, CancellationToken ct)
    {
        using var req = await CreateRequestAsync(tenant, HttpMethod.Get, $"groups/{workspaceId}/reports/{reportId}", ct);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "get report", ct);
        var r = await res.Content.ReadFromJsonAsync<ReportDto>(Json, ct) ?? throw new EmbedException("platform-error", "Empty report response.");
        return new PowerBiReport(r.Id, r.Name, r.EmbedUrl, r.DatasetId, r.WebUrl);
    }

    public async Task<PowerBiDataset> GetDatasetAsync(BiTenant tenant, Guid workspaceId, Guid datasetId, CancellationToken ct)
    {
        using var req = await CreateRequestAsync(tenant, HttpMethod.Get, $"groups/{workspaceId}/datasets/{datasetId}", ct);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "read the dataset", ct);
        var d = await res.Content.ReadFromJsonAsync<DatasetDto>(Json, ct) ?? throw new EmbedException("platform-error", "Empty dataset response.");
        return new PowerBiDataset(d.Id, d.Name, d.IsEffectiveIdentityRequired, d.IsEffectiveIdentityRolesRequired);
    }

    public async Task<int> CountReportsAsync(BiTenant tenant, Guid workspaceId, CancellationToken ct)
    {
        using var req = await CreateRequestAsync(tenant, HttpMethod.Get, $"groups/{workspaceId}/reports", ct);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "read the workspace", ct);
        var list = await res.Content.ReadFromJsonAsync<ListDto<ReportDto>>(Json, ct);
        return list?.Value.Count ?? 0;
    }

    /// <summary>
    /// Uploads a .pbix into the workspace (Imports API). The dataset display name is unique per dashboard,
    /// so CreateOrOverwrite replaces the same report when a new version of the file is published.
    /// Returns the import ID to poll.
    /// </summary>
    public async Task<Guid> ImportPbixAsync(BiTenant tenant, Guid workspaceId, Stream pbix, string displayName, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(displayName.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase) ? displayName : displayName + ".pbix");
        using var req = await CreateRequestAsync(tenant, HttpMethod.Post,
            $"groups/{workspaceId}/imports?datasetDisplayName={name}&nameConflict=CreateOrOverwrite", ct);

        var file = new StreamContent(pbix);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content = new MultipartFormDataContent { { file, "file", Uri.UnescapeDataString(name) } };

        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "import the .pbix", ct);
        var dto = await res.Content.ReadFromJsonAsync<IdDto>(Json, ct) ?? throw new EmbedException("platform-error", "Empty import response.");
        return dto.Id;
    }

    public async Task<PowerBiImport> GetImportAsync(BiTenant tenant, Guid workspaceId, Guid importId, CancellationToken ct)
    {
        using var req = await CreateRequestAsync(tenant, HttpMethod.Get, $"groups/{workspaceId}/imports/{importId}", ct);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "check the import", ct);
        var dto = await res.Content.ReadFromJsonAsync<ImportDto>(Json, ct) ?? throw new EmbedException("platform-error", "Empty import status.");
        var error = dto.Error is null ? null : $"{dto.Error.Code}: {dto.Error.Details?.ToString() ?? dto.Error.Message}";
        return new PowerBiImport(dto.Id, dto.ImportState ?? "Publishing", dto.Reports?.FirstOrDefault()?.Id, dto.Datasets?.FirstOrDefault()?.Id, error);
    }

    /// <summary>Multi-resource embed token (GenerateToken V2) for one report, its dataset and optional RLS identity.</summary>
    public async Task<PowerBiToken> GenerateTokenAsync(BiTenant tenant, Guid workspaceId, Guid reportId, Guid datasetId,
        IReadOnlyList<RlsIdentity> identities, CancellationToken ct)
    {
        var body = new GenerateTokenRequest(
            Reports: [new ReportRef(reportId)],
            Datasets: [new DatasetRef(datasetId)], // xmlaPermissions left out: defaults to Off (only "Off"/"ReadOnly" are valid)
            TargetWorkspaces: [new WorkspaceRef(workspaceId)],
            Identities: identities.Count == 0 ? null : identities.Select(i => new IdentityRef(i.Username, i.Roles, i.Datasets)).ToList());

        using var req = await CreateRequestAsync(tenant, HttpMethod.Post, "GenerateToken", ct);
        req.Content = JsonContent.Create(body, options: Json);
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(res, "generate embed token", ct);
        var token = await res.Content.ReadFromJsonAsync<TokenDto>(Json, ct) ?? throw new EmbedException("platform-error", "Empty token response.");
        return new PowerBiToken(token.Token, token.Expiration);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(BiTenant tenant, HttpMethod method, string path, CancellationToken ct)
    {
        var accessToken = await tokens.GetAccessTokenAsync(tenant, ct);
        var req = new HttpRequestMessage(method, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage res, string action, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        log.LogWarning("Power BI {Action} failed: {Status} {Body}", action, (int)res.StatusCode, body);

        // Power BI explains most failures in error.code / error.message (or a nested details list); show that.
        string? reason = null;
        try
        {
            var env = JsonSerializer.Deserialize<ErrorEnvelope>(body, Json);
            if (env?.Error is { } e)
            {
                var details = e.Details is { ValueKind: JsonValueKind.Array } arr
                    ? string.Join(" ", arr.EnumerateArray().Select(x => x.TryGetProperty("message", out var m) ? m.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                    : null;
                reason = string.Join(": ", new[] { e.Code, string.IsNullOrWhiteSpace(details) ? e.Message : details }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }
        catch (JsonException) { /* not JSON */ }
        if (string.IsNullOrWhiteSpace(reason) && body.Length is > 0 and < 400) reason = body.Trim();

        var hint = (int)res.StatusCode switch
        {
            401 => " The service principal is not allowed to use Power BI APIs; check the tenant setting 'Service principals can use Fabric APIs'.",
            403 or 404 => " The service principal may not be a Member or Admin of the workspace, or the ID is wrong.",
            400 when body.Contains("effective identity", StringComparison.OrdinalIgnoreCase) || body.Contains("roles", StringComparison.OrdinalIgnoreCase)
                => " This is a row-level security mismatch: the report's roles and the group's RLS role must match exactly.",
            _ => "",
        };
        throw new EmbedException("platform-error", $"Power BI could not {action} ({(int)res.StatusCode}){(reason is null ? "." : $": {reason}.")}{hint}");
    }

    private sealed record IdDto(Guid Id);
    private sealed record DatasetDto(Guid Id, string Name, bool IsEffectiveIdentityRequired, bool IsEffectiveIdentityRolesRequired);
    private sealed record ErrorEnvelope(ErrorBody? Error);
    private sealed record ErrorBody(string? Code, string? Message, JsonElement? Details);
    private sealed record ReportDto(Guid Id, string Name, string EmbedUrl, Guid DatasetId, string? WebUrl);
    private sealed record ListDto<T>(List<T> Value);
    private sealed record TokenDto(string Token, DateTimeOffset Expiration);
    private sealed record ImportDto(Guid Id, string? ImportState, List<IdDto>? Reports, List<IdDto>? Datasets, ImportErrorDto? Error);
    private sealed record ImportErrorDto(string? Code, string? Message, JsonElement? Details);
    private sealed record GenerateTokenRequest(List<ReportRef> Reports, List<DatasetRef> Datasets, List<WorkspaceRef> TargetWorkspaces, List<IdentityRef>? Identities);
    private sealed record ReportRef(Guid Id);
    private sealed record DatasetRef(Guid Id);
    private sealed record WorkspaceRef(Guid Id);
    private sealed record IdentityRef(string Username, IReadOnlyList<string> Roles, IReadOnlyList<string> Datasets);
}

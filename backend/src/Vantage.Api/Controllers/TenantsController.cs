using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Secrets;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Tenants;

namespace Vantage.Api.Controllers;

/// <summary>
/// Tenant Master: Power BI tenants (one service principal, one or more workspaces) and Tableau Server sites
/// (one Connected App). Secrets are write-only: they can be replaced but are never returned.
/// </summary>
[Route("api/admin/tenants")]
public sealed class TenantsController(CurrentUser current, AppDbContext db, ISecretStore secrets, TenantVerifier verifier, AuditWriter audit, TimeProvider clock)
    : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Tenants, PermissionLevel.View, ct) is { } denied) return denied;
        var tenants = await db.BiTenants.AsNoTracking().Include(t => t.Workspaces).OrderBy(t => t.Platform).ThenBy(t => t.Name).ToListAsync(ct);
        var dashboards = await db.Dashboards.Where(d => d.TenantId != null && d.Status != DashboardStatus.Retired)
            .GroupBy(d => d.TenantId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);

        var result = new List<object>();
        foreach (var t in tenants)
        {
            result.Add(new
            {
                t.Id, t.Name, platform = t.Platform.ToString(), t.IsActive,
                t.AzureTenantId, t.ClientId,
                t.ServerUrl, t.SiteContentUrl, t.ConnectedAppClientId, t.ConnectedAppSecretId, t.VerifyUserName, t.TableauApiVersion,
                secretUpdatedAtUtc = t.SecretName is null ? null : await secrets.GetUpdatedAtAsync(t.SecretName, ct),
                t.LastVerifiedAtUtc, t.LastVerifyPassed, checks = TenantVerifier.ReadReport(t.LastVerifyReport),
                workspaces = t.Workspaces.OrderBy(w => w.Name).Select(w => new { w.Id, w.Name, w.WorkspaceId, w.IsActive, w.ServicePrincipalAccess, w.OnDedicatedCapacity }),
                dashboards = dashboards.GetValueOrDefault(t.Id),
                t.CreatedAtUtc, t.UpdatedAtUtc,
            });
        }
        return Ok(result);
    }

    public sealed record WorkspaceBody(int? Id, string Name, Guid WorkspaceId, bool IsActive);

    public sealed record TenantBody(
        string Name, string Platform, bool IsActive,
        string? AzureTenantId, string? ClientId,
        string? ServerUrl, string? SiteContentUrl, string? ConnectedAppClientId, string? ConnectedAppSecretId, string? VerifyUserName, string? TableauApiVersion,
        string? Secret, List<WorkspaceBody>? Workspaces);

    [HttpPost]
    public async Task<IActionResult> Create(TenantBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Tenants, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            if (!Enum.TryParse<BiPlatform>(body.Platform, true, out var platform)) throw new RuleException("Platform must be PowerBi or Tableau.");
            if (string.IsNullOrWhiteSpace(body.Secret)) throw new RuleException(platform == BiPlatform.PowerBi ? "Enter the client secret." : "Enter the Connected App secret value.");
            var tenant = new BiTenant { Platform = platform, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
            await ApplyAsync(tenant, body, ct);
            db.BiTenants.Add(tenant);
            await db.SaveChangesAsync(ct);
            tenant.SecretName = $"tenant-{tenant.Id}";
            await secrets.SetAsync(tenant.SecretName, body.Secret, Current.UserId, ct);
            audit.Add("tenant.created", "BiTenant", tenant.Id, details: new { tenant.Name, platform = tenant.Platform.ToString() });
            await db.SaveChangesAsync(ct);
            return Ok(new { tenant.Id });
        });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, TenantBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Tenants, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var tenant = await db.BiTenants.Include(t => t.Workspaces).SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw new KeyNotFoundException();
            await ApplyAsync(tenant, body, ct);
            tenant.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            // Any change means the last verification no longer applies.
            tenant.LastVerifyPassed = null;
            tenant.LastVerifiedAtUtc = null;
            tenant.LastVerifyReport = null;
            if (!string.IsNullOrWhiteSpace(body.Secret))
            {
                tenant.SecretName ??= $"tenant-{tenant.Id}";
                await secrets.SetAsync(tenant.SecretName, body.Secret, Current.UserId, ct);
            }
            audit.Add("tenant.updated", "BiTenant", tenant.Id, details: new { tenant.Name, secretReplaced = !string.IsNullOrWhiteSpace(body.Secret) });
            await db.SaveChangesAsync(ct);
            return Ok(new { tenant.Id });
        });
    }

    /// <summary>Runs every connection and permission check and stores the result.</summary>
    [HttpPost("{id:int}/verify")]
    public async Task<IActionResult> Verify(int id, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.Tenants, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            var report = await verifier.VerifyAsync(id, ct);
            audit.Add("tenant.verified", "BiTenant", id, details: new { report.Passed, failed = report.Checks.Where(c => c.Result == CheckResult.Fail).Select(c => c.Name) });
            await db.SaveChangesAsync(ct);
            return Ok(report);
        });
    }

    private async Task ApplyAsync(BiTenant t, TenantBody b, CancellationToken ct)
    {
        // Entra ID shows two values for a client secret: "Value" (what we need) and "Secret ID" (a GUID). The GUID is a common mix-up.
        if (t.Platform == BiPlatform.PowerBi && Guid.TryParse(b.Secret?.Trim(), out _))
            throw new RuleException("That looks like the secret's \"Secret ID\" (a GUID). Paste the secret's \"Value\" instead. Entra ID shows it only once, when the secret is created; if you didn't copy it then, create a new secret.");

        var name = (b.Name ?? "").Trim();
        if (name.Length is 0 or > 100) throw new RuleException("Tenant name is required, up to 100 characters.");
        if (await db.BiTenants.AnyAsync(x => x.Name == name && x.Id != t.Id, ct)) throw new RuleException($"A tenant named '{name}' already exists.");
        t.Name = name;
        t.IsActive = b.IsActive;

        if (t.Platform == BiPlatform.PowerBi)
        {
            t.AzureTenantId = RequireGuid(b.AzureTenantId, "Azure tenant ID");
            t.ClientId = RequireGuid(b.ClientId, "Client (application) ID");
            var incoming = b.Workspaces ?? [];
            if (incoming.Count == 0) throw new RuleException("Add at least one workspace.");
            if (incoming.Select(w => w.WorkspaceId).Distinct().Count() != incoming.Count) throw new RuleException("The same workspace ID is listed twice.");
            foreach (var w in incoming)
            {
                if (w.WorkspaceId == Guid.Empty) throw new RuleException("Every workspace needs its workspace ID (a GUID from the workspace URL).");
                var wsName = (w.Name ?? "").Trim();
                if (wsName.Length is 0 or > 100) throw new RuleException("Every workspace needs a name, up to 100 characters.");
                var existing = w.Id is { } wid ? t.Workspaces.SingleOrDefault(x => x.Id == wid) : null;
                if (existing is null)
                {
                    t.Workspaces.Add(new PowerBiWorkspace { Name = wsName, WorkspaceId = w.WorkspaceId, IsActive = w.IsActive });
                }
                else
                {
                    if (existing.WorkspaceId != w.WorkspaceId && await db.Dashboards.AnyAsync(d => d.WorkspaceId == existing.Id, ct))
                        throw new RuleException($"Workspace '{existing.Name}' has dashboards, so its workspace ID can't change. Add a new workspace instead.");
                    existing.Name = wsName;
                    existing.WorkspaceId = w.WorkspaceId;
                    existing.IsActive = w.IsActive;
                }
            }
            // Workspaces left out of the list are deactivated, never deleted (dashboards may point at them).
            foreach (var ws in t.Workspaces.Where(x => x.Id != 0 && incoming.All(i => i.Id != x.Id)))
                ws.IsActive = false;
        }
        else
        {
            var url = (b.ServerUrl ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                throw new RuleException("Server URL must be a full address, e.g. https://tableau.company.com.");
            t.ServerUrl = url;
            t.SiteContentUrl = (b.SiteContentUrl ?? "").Trim();
            t.ConnectedAppClientId = Required(b.ConnectedAppClientId, "Connected App client ID");
            t.ConnectedAppSecretId = Required(b.ConnectedAppSecretId, "Secret ID");
            t.VerifyUserName = string.IsNullOrWhiteSpace(b.VerifyUserName) ? null : b.VerifyUserName.Trim();
            t.TableauApiVersion = string.IsNullOrWhiteSpace(b.TableauApiVersion) ? "3.21" : b.TableauApiVersion.Trim();
        }
    }

    private static string RequireGuid(string? value, string label) =>
        Guid.TryParse(value?.Trim(), out var g) ? g.ToString() : throw new RuleException($"{label} must be a GUID, e.g. 8eb1788b-cb4f-4ace-bd37-91903c11e2b4.");

    private static string Required(string? value, string label) =>
        string.IsNullOrWhiteSpace(value) ? throw new RuleException($"{label} is required.") : value.Trim();
}

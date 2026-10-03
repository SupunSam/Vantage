using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;

namespace Vantage.Infrastructure.Services;

public sealed class NoAccessException(int dashboardId, string reason) : Exception(reason)
{
    public int DashboardId { get; } = dashboardId;
    /// <summary>no-group, inactive, ownerless</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Decides whether a user may open a dashboard and builds the embed details.
/// Group membership is the only route in, for every user including Super Admins.
/// </summary>
public sealed class EmbedService(AppDbContext db, PowerBiClient powerBi, TableauTokenService tableau, AuditWriter audit, TimeProvider clock)
{
    public async Task<EmbedInfo> GetEmbedAsync(int dashboardId, int userId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards
            .Include(d => d.Tenant)
            .Include(d => d.Workspace)
            .SingleOrDefaultAsync(d => d.Id == dashboardId, ct)
            ?? throw new KeyNotFoundException($"Dashboard {dashboardId} not found.");

        if (dashboard.Status != DashboardStatus.Active)
            throw new NoAccessException(dashboardId, "inactive");

        var membership = await db.GroupMembers
            .Where(m => m.DashboardId == dashboardId && m.UserId == userId && m.RemovedAtUtc == null && m.Group.Status == GroupStatus.Active)
            .Select(m => new { m.GroupId, m.Group.RlsValue, m.User.Email, m.User.TableauUserName })
            .SingleOrDefaultAsync(ct);

        if (membership is null)
            throw new NoAccessException(dashboardId, "no-group");

        EmbedInfo info = dashboard.Type switch
        {
            DashboardType.PowerBi => await PowerBiAsync(dashboard, membership.Email, membership.RlsValue, ct),
            DashboardType.Tableau => await TableauAsync(dashboard, membership.TableauUserName, ct),
            DashboardType.GenAi => new EmbedInfo("genai", dashboard.Id, dashboard.Name, $"/api/dashboards/{dashboard.Id}/genai-content", null, null, null, null),
            _ => throw new EmbedException("not-configured", "Unknown dashboard type."),
        };

        var now = clock.GetUtcNow().UtcDateTime;
        dashboard.LastViewedAtUtc = now;
        dashboard.InactivityFlaggedAtUtc = null;
        db.DashboardViews.Add(new DashboardView { DashboardId = dashboard.Id, UserId = userId, Source = ViewSource.Portal, ViewedAtUtc = now });
        audit.Add("embed.token-issued", "Dashboard", dashboard.Id, dashboard.Id,
            new { type = info.Type, groupId = membership.GroupId, rls = dashboard.RlsEnabled ? membership.RlsValue : null, expiresAt = info.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return info;
    }

    private async Task<EmbedInfo> PowerBiAsync(Dashboard d, string email, string? rlsValue, CancellationToken ct)
    {
        if (d.Tenant is null || d.Workspace is null || d.PowerBiReportId is null)
            throw new EmbedException("not-linked", "This dashboard is not linked to a Power BI report yet.");

        var report = await powerBi.GetReportAsync(d.Tenant, d.Workspace.WorkspaceId, d.PowerBiReportId.Value, ct);
        if (d.PowerBiDatasetId != report.DatasetId) d.PowerBiDatasetId = report.DatasetId;

        var dataset = await powerBi.GetDatasetAsync(d.Tenant, d.Workspace.WorkspaceId, report.DatasetId, ct);
        d.RlsEnabled = dataset.RolesRequired; // keep the flag in step with the model
        var identities = RlsIdentityBuilder.Build(dataset.IdentityRequired, dataset.RolesRequired, email, rlsValue, report.DatasetId);
        var token = await powerBi.GenerateTokenAsync(d.Tenant, d.Workspace.WorkspaceId, report.Id, report.DatasetId, identities, ct);
        return new EmbedInfo("powerbi", d.Id, d.Name, report.EmbedUrl, token.Token, token.Expiration, report.Id.ToString(), null);
    }

    private async Task<EmbedInfo> TableauAsync(Dashboard d, string? tableauUserName, CancellationToken ct)
    {
        if (d.Tenant is null || string.IsNullOrWhiteSpace(d.TableauViewUrl))
            throw new EmbedException("not-linked", "This dashboard has no Tableau view URL yet.");

        var (token, expires) = await tableau.CreateTokenAsync(d.Tenant, tableauUserName ?? "", ct);
        return new EmbedInfo("tableau", d.Id, d.Name, d.TableauViewUrl, token, expires, null, TableauTokenService.ScriptUrl(d.Tenant));
    }
}

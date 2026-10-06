using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.GenAi;

namespace Vantage.Infrastructure.Services;

public sealed class NoAccessException(int dashboardId, string reason) : Exception(reason)
{
    public int DashboardId { get; } = dashboardId;
    /// <summary>no-group, inactive, ownerless</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Decides whether a user may open a dashboard and builds the embed details.
/// Group membership is the only route in for consuming a dashboard, for every user including Super Admins.
/// Super Admins administer rather than consume, so they preview through <see cref="PreviewAsync"/> instead, which
/// needs no membership and leaves no trace in usage.
/// </summary>
public sealed class EmbedService(AppDbContext db, PowerBiClient powerBi, TableauTokenService tableau, GenAiService genAi, AuditWriter audit, TimeProvider clock)
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
        if (await db.BiTypes.AnyAsync(t => t.Type == dashboard.Type && !t.IsEnabled && t.HideWhenInactive, ct))
            throw new NoAccessException(dashboardId, "inactive");

        var membership = await db.GroupMembers
            .Where(m => m.DashboardId == dashboardId && m.UserId == userId && m.RemovedAtUtc == null && m.Group.Status == GroupStatus.Active)
            .Select(m => new { m.GroupId, m.Group.RlsValue, m.User.Email, m.User.TableauUserName })
            .SingleOrDefaultAsync(ct);

        if (membership is null)
            throw new NoAccessException(dashboardId, "no-group");

        var info = await BuildAsync(dashboard, membership.Email, membership.TableauUserName, membership.RlsValue, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        dashboard.LastViewedAtUtc = now;
        dashboard.InactivityFlaggedAtUtc = null;
        db.DashboardViews.Add(new DashboardView { DashboardId = dashboard.Id, UserId = userId, Source = ViewSource.Portal, ViewedAtUtc = now });
        audit.Add("embed.token-issued", "Dashboard", dashboard.Id, dashboard.Id,
            new { type = info.Type, groupId = membership.GroupId, rls = dashboard.RlsEnabled ? membership.RlsValue : null, expiresAt = info.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return info;
    }

    /// <summary>
    /// Embed details for a Super Admin checking that a dashboard works, as a member of the chosen group would see it
    /// (that group's RLS role, with the Super Admin's own email as the identity). No membership is needed and none is
    /// created. It is not a view: it doesn't count in usage or reset the inactivity flag. Each preview is audited.
    /// </summary>
    public async Task<EmbedInfo> PreviewAsync(int dashboardId, int groupId, int adminUserId, CancellationToken ct)
    {
        var dashboard = await db.Dashboards.Include(d => d.Tenant).Include(d => d.Workspace).SingleOrDefaultAsync(d => d.Id == dashboardId, ct)
            ?? throw new KeyNotFoundException($"Dashboard {dashboardId} not found.");
        if (dashboard.Status != DashboardStatus.Active) throw new RuleException("A preview is available once the dashboard is Active.");
        var group = await db.DashboardGroups.SingleOrDefaultAsync(g => g.Id == groupId && g.DashboardId == dashboardId && g.Status != GroupStatus.Retired, ct)
            ?? throw new KeyNotFoundException();
        if (dashboard.RlsEnabled && string.IsNullOrWhiteSpace(group.RlsValue))
            throw new RuleException($"{group.Name} has no RLS value, so a preview through it would fail. Set its RLS value first, or preview as another group.");
        var admin = await db.Users.Where(u => u.Id == adminUserId).Select(u => new { u.Email, u.TableauUserName }).SingleAsync(ct);

        var info = await BuildAsync(dashboard, admin.Email, admin.TableauUserName, group.RlsValue, ct);
        audit.Add("dashboard.previewed", "Dashboard", dashboard.Id, dashboard.Id,
            new { groupId = group.Id, group = group.Name, rls = dashboard.RlsEnabled ? group.RlsValue : null, expiresAt = info.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return info;
    }

    private async Task<EmbedInfo> BuildAsync(Dashboard dashboard, string email, string? tableauUserName, string? rlsValue, CancellationToken ct) => dashboard.Type switch
    {
        BiType.PowerBi => await PowerBiAsync(dashboard, email, rlsValue, ct),
        BiType.Tableau => await TableauAsync(dashboard, tableauUserName, ct),
        BiType.GenAi => await GenAiAsync(dashboard, ct),
        _ => throw new EmbedException("not-configured", "Unknown dashboard type."),
    };

    /// <summary>A signed link to the file on the separate GenAI origin; the viewer puts it in a sandboxed frame.</summary>
    private async Task<EmbedInfo> GenAiAsync(Dashboard d, CancellationToken ct)
    {
        var (url, expires) = await genAi.LinkAsync(d, ct);
        return new EmbedInfo("genai", d.Id, d.Name, url, null, expires, null, null);
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

    /// <summary>
    /// Tableau Server: a short-lived Connected App token for the viewer's Tableau user name (their Tableau account decides what they see inside).
    /// Tableau Public (no tenant): the public address and no token; the portal's groups decide who gets the link.
    /// The address is read again here, so a bad stored address gives a clear message instead of a broken page.
    /// </summary>
    private async Task<EmbedInfo> TableauAsync(Dashboard d, string? tableauUserName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(d.TableauViewUrl))
            throw new EmbedException("not-linked", "This dashboard has no Tableau view address yet.");

        TableauView view;
        try { view = TableauViewUrl.Parse(d.TableauViewUrl, d.Tenant?.ServerUrl, d.Tenant?.SiteContentUrl); }
        catch (RuleException ex) { throw new EmbedException("not-configured", ex.Message); }

        if (d.Tenant is null)
            return new EmbedInfo("tableau", d.Id, d.Name, view.Src, null, null, null, TableauViewUrl.ScriptUrl(view, null));

        var (token, expires) = await tableau.CreateTokenAsync(d.Tenant, tableauUserName ?? "", ct);
        return new EmbedInfo("tableau", d.Id, d.Name, view.Src, token, expires, null, TableauViewUrl.ScriptUrl(view, d.Tenant.ServerUrl));
    }
}

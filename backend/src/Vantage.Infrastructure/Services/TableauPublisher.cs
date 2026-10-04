using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;

namespace Vantage.Infrastructure.Services;

/// <param name="TenantId">The Tableau Server tenant, or null for a Tableau Public view.</param>
public sealed record PublishTableauRequest(
    string Name, string Code, string? Description, int? TenantId, string ViewUrl, int PrimaryOwnerId, int? BackupOwnerId, int? CategoryId,
    IReadOnlyList<string>? Tags = null, Audience Audience = Audience.Internal, DataClassification DataClassification = DataClassification.Internal);

/// <summary>
/// Publishes a Tableau dashboard, either a view on a Tableau Server tenant (embedded with a Connected App token) or a view on Tableau Public
/// (embedded as is). There is no import step, so it is live as soon as this returns, with the owners in the default group. Nothing is
/// created when anything fails. Row-level security belongs to Tableau (it follows each person's Tableau account), so the portal's RLS flag stays off
/// and the dashboard only ever has its default group (C15).
/// </summary>
public sealed class TableauPublisher(
    AppDbContext db, DashboardFactory factory, DashboardMasterService master, AuditWriter audit, NotificationService notifications, TimeProvider clock)
{
    public async Task<PublishStatus> PublishAsync(PublishTableauRequest req, int actorUserId, CancellationToken ct = default)
    {
        await ServiceTypes.EnsureAllowedAsync(db, DashboardType.Tableau, null, ct);
        var tenant = await ResolveTenantAsync(req.TenantId, ct);
        var view = TableauViewUrl.Parse(req.ViewUrl, tenant?.ServerUrl, tenant?.SiteContentUrl);
        EnsureClassificationFits(view.IsPublic, req.DataClassification);
        await master.ValidateCategoryAsync(req.CategoryId, ct);
        var tags = DashboardMasterService.ValidateTags(req.Tags);
        await master.ValidateOwnersAsync(req.PrimaryOwnerId, req.BackupOwnerId, ct);

        var dashboard = await factory.CreateAsync(new Dashboard
        {
            Code = req.Code.Trim(), Name = req.Name.Trim(), Description = req.Description?.Trim(),
            Type = DashboardType.Tableau, Status = DashboardStatus.Publishing, CategoryId = req.CategoryId, RlsEnabled = false,
            TenantId = tenant?.Id, TableauViewUrl = view.Src,
            PrimaryOwnerId = req.PrimaryOwnerId, BackupOwnerId = req.BackupOwnerId == req.PrimaryOwnerId ? null : req.BackupOwnerId,
            Audience = req.Audience, DataClassification = req.DataClassification,
            Tags = tags.Select(t => new DashboardTag { Tag = t }).ToList(),
        }, null, actorUserId, ct);

        dashboard.Status = DashboardStatus.Active;
        dashboard.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Add("dashboard.published", "Dashboard", dashboard.Id, dashboard.Id,
            new { type = "Tableau", mode = view.IsPublic ? "Public" : "Server", host = view.Host, workbook = view.Workbook, view = view.View, tenant = tenant?.Name });
        notifications.Notify(new[] { dashboard.PrimaryOwnerId, dashboard.BackupOwnerId, dashboard.CreatedByUserId }.OfType<int>().Distinct(),
            "publish.succeeded", $"{dashboard.Name} is published", "It's live for the members of its groups.", $"/dashboards/{dashboard.Id}");
        await db.SaveChangesAsync(ct);

        var group = await db.DashboardGroups.Where(g => g.DashboardId == dashboard.Id && g.IsDefault).Select(g => g.Name).SingleAsync(ct);
        return new PublishStatus(dashboard.Id, dashboard.Name, "Active", null, null, group, Warning(tenant, view));
    }

    /// <summary>Null for Tableau Public. A Server tenant must be a Tableau tenant, and active. It need not be verified (publishing only stores the address).</summary>
    public async Task<BiTenant?> ResolveTenantAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null or 0) return null;
        var tenant = await db.BiTenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct) ?? throw new RuleException("That Tableau tenant doesn't exist.");
        if (tenant.Platform != BiPlatform.Tableau) throw new RuleException($"{tenant.Name} is a Power BI tenant. Choose a Tableau Server tenant, or Tableau Public.");
        if (!tenant.IsActive) throw new RuleException($"{tenant.Name} is switched off in Tenants.");
        return tenant;
    }

    /// <summary>A Tableau Public view is visible to anyone on the internet who has its address, so only Public data may go there.</summary>
    public static void EnsureClassificationFits(bool isPublic, DataClassification classification)
    {
        if (isPublic && classification != DataClassification.Public)
            throw new RuleException("A Tableau Public view can be opened by anyone who has its address, so the dashboard's data classification must be Public. Use a Tableau Server tenant for anything else.");
    }

    public static string? Warning(BiTenant? tenant, TableauView view)
    {
        if (view.IsPublic) return "This is a Tableau Public view: the portal controls who sees it here, but anyone who has its address can open it on Tableau Public.";
        return tenant is { LastVerifyPassed: true } ? null : $"{tenant!.Name} hasn't passed Verify in Tenants yet, so the portal can't promise it can sign people in. Run Verify there before sharing the dashboard.";
    }
}

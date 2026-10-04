using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Storage;

namespace Vantage.Infrastructure.Services;

public sealed record PublishPowerBiRequest(
    string Name, string Code, string? Description, int WorkspaceId, int PrimaryOwnerId, int? BackupOwnerId,
    bool RlsEnabled, string? DefaultGroupRlsValue, int? CategoryId, string FileName,
    IReadOnlyList<string>? Tags = null, Audience Audience = Audience.Internal, DataClassification DataClassification = DataClassification.Internal);

public sealed record PublishStatus(int DashboardId, string Name, string Status, string? Error, Guid? ReportId, string? DefaultGroup, string? Warning = null);

/// <summary>
/// Publishes a .pbix: creates the dashboard (status Publishing) with its default group, keeps the file as version 1,
/// imports it into the chosen workspace as the service principal, then moves the dashboard to Active (with the
/// published report ID) or Failed when the import finishes. Tableau and GenAI publishing are added in step 4.
/// </summary>
public sealed class PublishingService(
    AppDbContext db, DashboardFactory factory, DashboardMasterService master, PowerBiClient powerBi, IFileStore files, AuditWriter audit,
    NotificationService notifications, TimeProvider clock, ILogger<PublishingService> log)
{
    public async Task<PublishStatus> PublishPowerBiAsync(PublishPowerBiRequest req, Stream pbix, int actorUserId, CancellationToken ct)
    {
        if (!req.FileName.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .pbix file.");
        await ServiceTypes.EnsureAllowedAsync(db, DashboardType.PowerBi, pbix.CanSeek ? pbix.Length : null, ct);

        var workspace = await db.PowerBiWorkspaces.Include(w => w.Tenant)
            .SingleOrDefaultAsync(w => w.Id == req.WorkspaceId && w.IsActive && w.Tenant.IsActive, ct)
            ?? throw new ArgumentException("Choose an active workspace from the tenant master.");
        await master.ValidateCategoryAsync(req.CategoryId, ct);
        var tags = DashboardMasterService.ValidateTags(req.Tags);
        await master.ValidateOwnersAsync(req.PrimaryOwnerId, req.BackupOwnerId, ct);

        // Fail fast on sign-in problems (missing secret, wrong client ID) so no dashboard record is created.
        await powerBi.EnsureSignInAsync(workspace.Tenant, ct);

        // Publishing again over a failed attempt reuses that dashboard and adds a new file version.
        var dashboard = await db.Dashboards.Include(d => d.Groups)
            .Include(d => d.Tags)
            .SingleOrDefaultAsync(d => d.Name == req.Name.Trim() && d.Status == DashboardStatus.Failed && d.Type == DashboardType.PowerBi, ct);
        if (dashboard is not null)
        {
            if (req.RlsEnabled && string.IsNullOrWhiteSpace(req.DefaultGroupRlsValue))
                throw new ArgumentException("RLS is on, so the default group needs an RLS value.");
            dashboard.Description = req.Description?.Trim();
            dashboard.TenantId = workspace.TenantId;
            dashboard.WorkspaceId = workspace.Id;
            dashboard.CategoryId = req.CategoryId;
            dashboard.RlsEnabled = req.RlsEnabled;
            dashboard.Audience = req.Audience;
            dashboard.DataClassification = req.DataClassification;
            dashboard.Tags.RemoveAll(_ => true);
            dashboard.Tags.AddRange(tags.Select(t => new DashboardTag { Tag = t }));
            dashboard.Status = DashboardStatus.Publishing;
            dashboard.LastError = null;
            dashboard.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            var defaultGroup = dashboard.Groups.Single(g => g.IsDefault);
            defaultGroup.RlsValue = string.IsNullOrWhiteSpace(req.DefaultGroupRlsValue) ? null : req.DefaultGroupRlsValue.Trim();
            audit.Add("dashboard.publish-retried", "Dashboard", dashboard.Id, dashboard.Id, new { file = req.FileName });
        }
        else
        {
            dashboard = await factory.CreateAsync(new Dashboard
            {
                Code = req.Code.Trim(), Name = req.Name.Trim(), Description = req.Description?.Trim(),
                Type = DashboardType.PowerBi, Status = DashboardStatus.Publishing,
                TenantId = workspace.TenantId, WorkspaceId = workspace.Id, CategoryId = req.CategoryId,
                RlsEnabled = req.RlsEnabled, PrimaryOwnerId = req.PrimaryOwnerId,
                BackupOwnerId = req.BackupOwnerId == req.PrimaryOwnerId ? null : req.BackupOwnerId,
                Audience = req.Audience, DataClassification = req.DataClassification,
                Tags = tags.Select(t => new DashboardTag { Tag = t }).ToList(),
            }, req.DefaultGroupRlsValue, actorUserId, ct);
        }

        // Keep the uploaded file as a new version (the last 3 are kept for download and restore).
        var versionNumber = (await db.DashboardVersions.Where(v => v.DashboardId == dashboard.Id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
        await db.DashboardVersions.Where(v => v.DashboardId == dashboard.Id && v.IsCurrent)
            .ExecuteUpdateAsync(u => u.SetProperty(v => v.IsCurrent, false), ct);
        var key = $"dashboards/{dashboard.Id}/v{versionNumber}/{Path.GetFileName(req.FileName)}";
        var (size, sha) = await files.SaveAsync(key, pbix, ct);
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = dashboard.Id, VersionNumber = versionNumber, FileKey = key, FileName = Path.GetFileName(req.FileName),
            SizeBytes = size, Sha256 = sha, IsCurrent = true, ScanStatus = ScanStatus.NotRequired,
            UploadedAtUtc = clock.GetUtcNow().UtcDateTime, UploadedByUserId = actorUserId,
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await using var stream = files.OpenRead(key);
            dashboard.PowerBiImportId = await powerBi.ImportPbixAsync(workspace.Tenant, workspace.WorkspaceId, stream, $"{dashboard.Code}-{dashboard.Id}", ct);
            audit.Add("dashboard.publish-started", "Dashboard", dashboard.Id, dashboard.Id,
                new { workspace = workspace.Name, importId = dashboard.PowerBiImportId, file = req.FileName, size });
        }
        catch (EmbedException ex)
        {
            log.LogWarning(ex, "Import of dashboard {Id} failed to start", dashboard.Id);
            Fail(dashboard, ex.Message);
        }
        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(dashboard.Id, ct);
    }

    /// <summary>Returns the publish status; while Publishing, asks Power BI how the import is going and updates the dashboard.</summary>
    public async Task<PublishStatus> GetStatusAsync(int dashboardId, CancellationToken ct)
    {
        var d = await db.Dashboards.Include(x => x.Tenant).Include(x => x.Workspace)
            .SingleOrDefaultAsync(x => x.Id == dashboardId, ct) ?? throw new KeyNotFoundException();

        if (d.Status == DashboardStatus.Publishing && d.PowerBiImportId is { } importId && d.Tenant is not null && d.Workspace is not null)
        {
            try
            {
                var import = await powerBi.GetImportAsync(d.Tenant, d.Workspace.WorkspaceId, importId, ct);
                if (import.State == "Succeeded" && import.ReportId is not null)
                {
                    d.PowerBiReportId = import.ReportId;
                    d.PowerBiDatasetId = import.DatasetId;
                    d.Status = DashboardStatus.Active;
                    if (import.DatasetId is { } dsId)
                    {
                        try
                        {
                            var ds = await powerBi.GetDatasetAsync(d.Tenant, d.Workspace.WorkspaceId, dsId, ct);
                            if (ds.RolesRequired != d.RlsEnabled)
                                audit.Add("dashboard.rls-corrected", "Dashboard", d.Id, d.Id, new { form = d.RlsEnabled, model = ds.RolesRequired });
                            d.RlsEnabled = ds.RolesRequired;
                        }
                        catch (EmbedException ex) { log.LogWarning(ex, "Could not read dataset of dashboard {Id}", d.Id); }
                    }
                    d.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;
                    d.LastError = null;
                    audit.Add("dashboard.published", "Dashboard", d.Id, d.Id, new { reportId = d.PowerBiReportId, datasetId = d.PowerBiDatasetId });
                    notifications.Notify(Recipients(d), "publish.succeeded", $"{d.Name} is published",
                        "It's live for the members of its groups.", $"/dashboards/{d.Id}");
                    await db.SaveChangesAsync(ct);
                }
                else if (import.State == "Failed")
                {
                    Fail(d, import.Error ?? "Power BI could not import the file.");
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (EmbedException ex)
            {
                // Transient problems leave the dashboard in Publishing; the next poll tries again.
                log.LogWarning(ex, "Checking the import of dashboard {Id} failed", d.Id);
            }
        }

        var group = await db.DashboardGroups.Where(g => g.DashboardId == d.Id && g.IsDefault).Select(g => new { g.Name, g.RlsValue }).SingleOrDefaultAsync(ct);
        string? warning = null;
        if (d.Status == DashboardStatus.Active && d.RlsEnabled && string.IsNullOrWhiteSpace(group?.RlsValue))
            warning = "This report has row-level security roles, but the default group has no RLS role yet, so its members can't open it. Set the group's RLS role to a role name from the report.";
        return new PublishStatus(d.Id, d.Name, d.Status.ToString(), d.LastError, d.PowerBiReportId, group?.Name, warning);
    }

    private void Fail(Dashboard d, string error)
    {
        d.Status = DashboardStatus.Failed;
        d.LastError = error.Length > 2000 ? error[..2000] : error;
        audit.Add("dashboard.publish-failed", "Dashboard", d.Id, d.Id, new { error = d.LastError });
        notifications.Notify(Recipients(d), "publish.failed", $"{d.Name} didn't publish", d.LastError, $"/dashboards/{d.Id}");
    }

    /// <summary>Publish notifications go to the owners and whoever started the publish.</summary>
    private static IEnumerable<int> Recipients(Dashboard d) =>
        new[] { d.PrimaryOwnerId, d.BackupOwnerId, d.CreatedByUserId }.OfType<int>();
}

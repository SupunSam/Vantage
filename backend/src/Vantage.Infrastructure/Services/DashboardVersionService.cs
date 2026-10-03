using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Storage;

namespace Vantage.Infrastructure.Services;

/// <summary>Where a Modify Dashboard (or restore) stands: Idle, Replacing, Succeeded or Failed.</summary>
public sealed record ReplaceStatus(int DashboardId, string State, int? VersionNumber, string? Message);

/// <summary>
/// Modify Dashboard: imports a new .pbix over the live report in the same workspace (CreateOrOverwrite on the
/// dashboard's own dataset name), so the report keeps its place and everything in the portal stays as it is:
/// details, groups, RLS values, owners, pins. The dashboard stays Active while the import runs. Each file is kept as
/// a version; only the last 3 are kept. Super Admins can download a version or restore it (re-imports that file).
/// </summary>
public sealed class DashboardVersionService(
    AppDbContext db, PowerBiClient powerBi, IFileStore files, AuditWriter audit, NotificationService notifications,
    TimeProvider clock, ILogger<DashboardVersionService> log)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Uploads a new .pbix and starts importing it over the live report.</summary>
    public async Task<ReplaceStatus> ReplaceAsync(int dashboardId, Stream pbix, string fileName, int actorUserId, CancellationToken ct = default)
    {
        if (!fileName.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase)) throw new RuleException("Choose a .pbix file.");
        var d = await LoadReplaceableAsync(dashboardId, ct);
        await powerBi.EnsureSignInAsync(d.Tenant!, ct); // fail fast before storing anything

        var number = await NextVersionAsync(d.Id, ct);
        var key = $"dashboards/{d.Id}/v{number}/{Path.GetFileName(fileName)}";
        var (size, sha) = await files.SaveAsync(key, pbix, ct);
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = d.Id, VersionNumber = number, FileKey = key, FileName = Path.GetFileName(fileName), SizeBytes = size, Sha256 = sha,
            IsCurrent = false, ScanStatus = ScanStatus.NotRequired, UploadedAtUtc = Now, UploadedByUserId = actorUserId,
        });
        await db.SaveChangesAsync(ct);
        return await StartImportAsync(d, number, key, "dashboard.replace-started", new { file = Path.GetFileName(fileName), size, version = number }, ct);
    }

    /// <summary>Re-imports a kept version over the live report. It becomes a new version (the history stays in order).</summary>
    public async Task<ReplaceStatus> RestoreAsync(int dashboardId, int versionNumber, int actorUserId, CancellationToken ct = default)
    {
        var d = await LoadReplaceableAsync(dashboardId, ct);
        var source = await db.DashboardVersions.AsNoTracking().SingleOrDefaultAsync(v => v.DashboardId == dashboardId && v.VersionNumber == versionNumber, ct)
            ?? throw new KeyNotFoundException();
        if (source.IsCurrent) throw new RuleException($"Version {versionNumber} is already the live version.");
        await powerBi.EnsureSignInAsync(d.Tenant!, ct);

        var number = await NextVersionAsync(d.Id, ct);
        var key = $"dashboards/{d.Id}/v{number}/{source.FileName}";
        long size;
        string sha;
        await using (var stream = files.OpenRead(source.FileKey)) (size, sha) = await files.SaveAsync(key, stream, ct);
        db.DashboardVersions.Add(new DashboardVersion
        {
            DashboardId = d.Id, VersionNumber = number, FileKey = key, FileName = source.FileName, SizeBytes = size, Sha256 = sha,
            IsCurrent = false, ScanStatus = ScanStatus.NotRequired, UploadedAtUtc = Now, UploadedByUserId = actorUserId,
            ScanReport = $"Restored from version {versionNumber}",
        });
        await db.SaveChangesAsync(ct);
        return await StartImportAsync(d, number, key, "dashboard.restore-started", new { fromVersion = versionNumber, version = number }, ct);
    }

    /// <summary>Checks the running import; on success makes the new version current, on failure keeps the old one live.</summary>
    public async Task<ReplaceStatus> GetStatusAsync(int dashboardId, CancellationToken ct = default)
    {
        var d = await db.Dashboards.Include(x => x.Tenant).Include(x => x.Workspace).SingleOrDefaultAsync(x => x.Id == dashboardId, ct)
            ?? throw new KeyNotFoundException();
        if (d.ReplaceImportId is not { } importId || d.ReplaceVersionNumber is not { } number)
            return new ReplaceStatus(d.Id, "Idle", null, null);

        PowerBiImport import;
        try
        {
            import = await powerBi.GetImportAsync(d.Tenant!, d.Workspace!.WorkspaceId, importId, ct);
        }
        catch (EmbedException ex)
        {
            log.LogWarning(ex, "Checking the replacement import of dashboard {Id} failed", d.Id);
            return new ReplaceStatus(d.Id, "Replacing", number, null); // try again on the next poll
        }

        if (import.State == "Succeeded" && import.ReportId is not null)
        {
            var versions = await db.DashboardVersions.Where(v => v.DashboardId == d.Id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
            foreach (var v in versions) v.IsCurrent = v.VersionNumber == number;
            if (d.PowerBiReportId != import.ReportId)
                audit.Add("dashboard.report-id-changed", "Dashboard", d.Id, d.Id, new { from = d.PowerBiReportId, to = import.ReportId });
            d.PowerBiReportId = import.ReportId;
            d.PowerBiDatasetId = import.DatasetId ?? d.PowerBiDatasetId;
            if (d.PowerBiDatasetId is { } dsId)
            {
                try
                {
                    var ds = await powerBi.GetDatasetAsync(d.Tenant!, d.Workspace!.WorkspaceId, dsId, ct);
                    if (ds.RolesRequired != d.RlsEnabled) audit.Add("dashboard.rls-corrected", "Dashboard", d.Id, d.Id, new { from = d.RlsEnabled, to = ds.RolesRequired });
                    d.RlsEnabled = ds.RolesRequired;
                }
                catch (EmbedException ex) { log.LogWarning(ex, "Could not read dataset of dashboard {Id}", d.Id); }
            }
            d.ReplaceImportId = null;
            d.ReplaceVersionNumber = null;
            d.LastError = null;
            d.UpdatedAtUtc = Now;
            TrimVersions(versions);
            audit.Add("dashboard.replaced", "Dashboard", d.Id, d.Id, new { version = number, reportId = d.PowerBiReportId });
            notifications.Notify(Recipients(d), "dashboard.replaced", $"{d.Name} was updated", $"Version {number} of the report is live.", $"/dashboards/{d.Id}");
            await db.SaveChangesAsync(ct);
            return new ReplaceStatus(d.Id, "Succeeded", number, $"Version {number} is live.");
        }

        if (import.State == "Failed")
        {
            var pending = await db.DashboardVersions.SingleOrDefaultAsync(v => v.DashboardId == d.Id && v.VersionNumber == number, ct);
            if (pending is not null)
            {
                files.Delete(pending.FileKey);
                db.DashboardVersions.Remove(pending);
            }
            var error = import.Error ?? "Power BI could not import the file.";
            d.ReplaceImportId = null;
            d.ReplaceVersionNumber = null;
            d.LastError = $"The new file wasn't imported, so the previous version is still live. {error}";
            if (d.LastError.Length > 2000) d.LastError = d.LastError[..2000];
            audit.Add("dashboard.replace-failed", "Dashboard", d.Id, d.Id, new { version = number, error });
            notifications.Notify(Recipients(d), "dashboard.replace-failed", $"{d.Name} wasn't updated", error, $"/dashboards/{d.Id}");
            await db.SaveChangesAsync(ct);
            return new ReplaceStatus(d.Id, "Failed", number, d.LastError);
        }

        return new ReplaceStatus(d.Id, "Replacing", number, null);
    }

    /// <summary>The stored file of a version, for download.</summary>
    public async Task<(Stream Content, string FileName)> OpenVersionAsync(int dashboardId, int versionNumber, CancellationToken ct = default)
    {
        var v = await db.DashboardVersions.AsNoTracking().SingleOrDefaultAsync(x => x.DashboardId == dashboardId && x.VersionNumber == versionNumber, ct)
            ?? throw new KeyNotFoundException();
        audit.Add("dashboard.version-downloaded", "Dashboard", dashboardId, dashboardId, new { version = versionNumber });
        await db.SaveChangesAsync(ct);
        return (files.OpenRead(v.FileKey), v.FileName);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Dashboard> LoadReplaceableAsync(int dashboardId, CancellationToken ct)
    {
        var d = await db.Dashboards.Include(x => x.Tenant).Include(x => x.Workspace).SingleOrDefaultAsync(x => x.Id == dashboardId, ct)
            ?? throw new KeyNotFoundException();
        if (d.Type != DashboardType.PowerBi) throw new RuleException("Only Power BI dashboards are modified with a new .pbix.");
        if (d.Status is not (DashboardStatus.Active or DashboardStatus.Inactive))
            throw new RuleException(d.Status == DashboardStatus.Failed
                ? "This dashboard never finished publishing. Publish it again from Publish Dashboard with the same name."
                : $"A {d.Status.ToString().ToLowerInvariant()} dashboard can't be modified.");
        if (d.ReplaceImportId is not null) throw new RuleException("A new version is already being imported. Wait for it to finish.");
        if (d.Tenant is null || d.Workspace is null || d.PowerBiReportId is null)
            throw new RuleException("This dashboard isn't linked to a Power BI report in a workspace.");
        if (!d.Tenant.IsActive || !d.Workspace.IsActive) throw new RuleException("Its tenant or workspace is inactive in the tenant master.");
        return d;
    }

    private async Task<int> NextVersionAsync(int dashboardId, CancellationToken ct) =>
        (await db.DashboardVersions.Where(v => v.DashboardId == dashboardId).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;

    private async Task<ReplaceStatus> StartImportAsync(Dashboard d, int number, string key, string action, object details, CancellationToken ct)
    {
        try
        {
            await using var stream = files.OpenRead(key);
            // Same dataset name as the first publish, so Power BI overwrites this dashboard's report and dataset.
            d.ReplaceImportId = await powerBi.ImportPbixAsync(d.Tenant!, d.Workspace!.WorkspaceId, stream, $"{d.Code}-{d.Id}", ct);
            d.ReplaceVersionNumber = number;
            audit.Add(action, "Dashboard", d.Id, d.Id, details);
            await db.SaveChangesAsync(ct);
            return new ReplaceStatus(d.Id, "Replacing", number, null);
        }
        catch (EmbedException)
        {
            var pending = await db.DashboardVersions.SingleAsync(v => v.DashboardId == d.Id && v.VersionNumber == number, ct);
            files.Delete(pending.FileKey);
            db.DashboardVersions.Remove(pending);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    /// <summary>Keeps the last <see cref="Rules.VersionsKept"/> versions (newest first in <paramref name="newestFirst"/>); older files are deleted.</summary>
    private void TrimVersions(List<DashboardVersion> newestFirst)
    {
        foreach (var old in newestFirst.Skip(Rules.VersionsKept).Where(v => !v.IsCurrent))
        {
            files.Delete(old.FileKey);
            db.DashboardVersions.Remove(old);
            audit.Add("dashboard.version-removed", "Dashboard", old.DashboardId, old.DashboardId, new { version = old.VersionNumber, reason = $"Only the last {Rules.VersionsKept} are kept" });
        }
    }

    private static IEnumerable<int> Recipients(Dashboard d) => new[] { d.PrimaryOwnerId, d.BackupOwnerId }.OfType<int>();
}

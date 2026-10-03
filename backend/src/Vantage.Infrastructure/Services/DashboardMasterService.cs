using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Storage;

namespace Vantage.Infrastructure.Services;

public sealed record DashboardDetailsInput(
    string Name, string? Description, int? CategoryId, int PrimaryOwnerId, int? BackupOwnerId,
    IReadOnlyList<string>? Tags, Audience Audience, DataClassification DataClassification,
    string? SharePointFolder, string? SharePointSubFolder);

/// <summary>
/// Dashboards Master: edits a dashboard's details after publishing (no re-upload), its thumbnail,
/// and re-reads the RLS flag from Power BI. The code stays fixed because the default group's name is built from it.
/// </summary>
public sealed class DashboardMasterService(
    AppDbContext db, OwnershipService ownership, PowerBiClient powerBi, IFileStore files, AuditWriter audit, TimeProvider clock)
{
    public async Task UpdateAsync(int id, DashboardDetailsInput input, int actorUserId, CancellationToken ct = default)
    {
        var d = await db.Dashboards.Include(x => x.Tags).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (d.Status == DashboardStatus.Retired) throw new RuleException("Retired dashboards can't be edited.");

        var name = (input.Name ?? "").Trim();
        if (name.Length == 0 || name.Length > Rules.DashboardNameMax) throw new RuleException($"Dashboard name is required, up to {Rules.DashboardNameMax} characters.");
        if (name != d.Name && await db.Dashboards.AnyAsync(x => x.Name == name && x.Id != id, ct))
            throw new RuleException($"A dashboard named '{name}' already exists.");
        var description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        if (description?.Length > Rules.DescriptionMax) throw new RuleException($"Description is up to {Rules.DescriptionMax} characters.");

        await ValidateCategoryAsync(input.CategoryId, ct);
        var tags = ValidateTags(input.Tags);
        var (primary, backup) = await ValidateOwnersAsync(input.PrimaryOwnerId, input.BackupOwnerId, ct);
        var folder = Clip(input.SharePointFolder, "SharePoint folder");
        var subFolder = Clip(input.SharePointSubFolder, "SharePoint sub-folder");

        var before = new
        {
            d.Name, d.Description, d.CategoryId, d.PrimaryOwnerId, d.BackupOwnerId,
            tags = d.Tags.Select(t => t.Tag).OrderBy(t => t).ToList(),
            audience = d.Audience.ToString(), classification = d.DataClassification.ToString(), d.SharePointFolder, d.SharePointSubFolder,
        };
        var oldOwners = new[] { d.PrimaryOwnerId, d.BackupOwnerId }.OfType<int>().ToList();

        d.Name = name;
        d.Description = description;
        d.CategoryId = input.CategoryId;
        d.PrimaryOwnerId = primary;
        d.BackupOwnerId = backup;
        d.Audience = input.Audience;
        d.DataClassification = input.DataClassification;
        d.SharePointFolder = folder;
        d.SharePointSubFolder = subFolder;
        d.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        d.Tags.RemoveAll(t => !tags.Contains(t.Tag, StringComparer.OrdinalIgnoreCase));
        foreach (var t in tags.Where(t => !d.Tags.Any(x => string.Equals(x.Tag, t, StringComparison.OrdinalIgnoreCase))))
            d.Tags.Add(new DashboardTag { Tag = t });

        // New owners go into the default group, unless they are already in one of this dashboard's groups.
        var newOwners = new[] { primary, backup }.OfType<int>().Except(oldOwners).ToList();
        if (newOwners.Count > 0)
        {
            var defaultGroupId = await db.DashboardGroups.Where(g => g.DashboardId == id && g.IsDefault).Select(g => g.Id).SingleAsync(ct);
            var alreadyIn = await db.GroupMembers.Where(m => m.DashboardId == id && m.RemovedAtUtc == null && newOwners.Contains(m.UserId)).Select(m => m.UserId).ToListAsync(ct);
            foreach (var userId in newOwners.Except(alreadyIn))
                db.GroupMembers.Add(new GroupMember { GroupId = defaultGroupId, DashboardId = id, UserId = userId, Source = MembershipSource.OwnerAuto, AddedAtUtc = clock.GetUtcNow().UtcDateTime, AddedByUserId = actorUserId });
        }
        foreach (var userId in oldOwners.Union(newOwners).Distinct())
            await ownership.SyncOwnerRoleAsync(userId, ct);

        audit.Add("dashboard.updated", "Dashboard", id, id, new
        {
            before,
            after = new { d.Name, d.Description, d.CategoryId, d.PrimaryOwnerId, d.BackupOwnerId, tags, audience = d.Audience.ToString(), classification = d.DataClassification.ToString(), d.SharePointFolder, d.SharePointSubFolder },
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Checks the image, stores it under a new key (so browsers never show a stale copy) and points the dashboard at it.</summary>
    public async Task<string> SetThumbnailAsync(int id, byte[] data, CancellationToken ct = default)
    {
        var info = Thumbnails.Validate(data);
        var d = await db.Dashboards.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        var key = $"thumbnails/{id}/{Guid.NewGuid():N}{info.Extension}";
        using (var ms = new MemoryStream(data)) await files.SaveAsync(key, ms, ct);
        audit.Add("dashboard.thumbnail-set", "Dashboard", id, id, new { info.Width, info.Height, bytes = data.Length });
        d.ThumbnailKey = key;
        d.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return key;
    }

    public async Task RemoveThumbnailAsync(int id, CancellationToken ct = default)
    {
        var d = await db.Dashboards.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (d.ThumbnailKey is null) return;
        d.ThumbnailKey = null;
        d.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Add("dashboard.thumbnail-removed", "Dashboard", id, id);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reads the semantic model from Power BI and sets the dashboard's RLS flag to match it.</summary>
    public async Task<(bool RlsEnabled, bool Changed)> SyncRlsAsync(int id, CancellationToken ct = default)
    {
        var d = await db.Dashboards.Include(x => x.Tenant).Include(x => x.Workspace).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (d.Type != DashboardType.PowerBi) throw new RuleException("Only Power BI dashboards have row-level security roles to check.");
        if (d.Tenant is null || d.Workspace is null || d.PowerBiReportId is null)
            throw new RuleException("This dashboard isn't linked to a published Power BI report yet.");

        var report = await powerBi.GetReportAsync(d.Tenant, d.Workspace.WorkspaceId, d.PowerBiReportId.Value, ct);
        var dataset = await powerBi.GetDatasetAsync(d.Tenant, d.Workspace.WorkspaceId, report.DatasetId, ct);
        var changed = d.RlsEnabled != dataset.RolesRequired;
        if (changed) audit.Add("dashboard.rls-corrected", "Dashboard", id, id, new { from = d.RlsEnabled, to = dataset.RolesRequired });
        d.RlsEnabled = dataset.RolesRequired;
        d.PowerBiDatasetId = report.DatasetId;
        await db.SaveChangesAsync(ct);
        return (d.RlsEnabled, changed);
    }

    // ---------------------------------------------------------------- shared checks (also used by publishing)

    public async Task ValidateCategoryAsync(int? categoryId, CancellationToken ct)
    {
        if (categoryId is null)
        {
            if (await db.Categories.AnyAsync(ct)) throw new RuleException("Choose a category (at least the primary one).");
            throw new RuleException("Create a category in Categories first; every dashboard sits in one.");
        }
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct)) throw new RuleException("The chosen category no longer exists.");
    }

    public static List<string> ValidateTags(IEnumerable<string>? input)
    {
        var tags = Rules.NormalizeTags(input);
        if (tags.Count > Rules.TagsPerDashboardMax) throw new RuleException($"Up to {Rules.TagsPerDashboardMax} tags per dashboard.");
        var bad = tags.FirstOrDefault(t => !Rules.IsValidTag(t));
        if (bad is not null) throw new RuleException($"Tag '{bad}' isn't valid: tags are letters and digits only, up to {Rules.TagMax} characters.");
        return tags;
    }

    public async Task<(int Primary, int? Backup)> ValidateOwnersAsync(int primaryId, int? backupId, CancellationToken ct)
    {
        if (backupId == primaryId) backupId = null;
        var ids = new[] { primaryId, backupId }.OfType<int>().ToList();
        var active = await db.Users.Where(u => ids.Contains(u.Id) && u.Status == UserStatus.Active).Select(u => u.Id).ToListAsync(ct);
        if (!active.Contains(primaryId)) throw new RuleException("The primary owner must be an active user.");
        if (backupId is { } b && !active.Contains(b)) throw new RuleException("The backup owner must be an active user.");
        return (primaryId, backupId);
    }

    private static string? Clip(string? value, string label)
    {
        var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (v?.Length > Rules.SharePointPathMax) throw new RuleException($"{label} is up to {Rules.SharePointPathMax} characters.");
        return v;
    }
}

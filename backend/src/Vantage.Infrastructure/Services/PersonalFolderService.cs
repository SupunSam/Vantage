using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

public sealed record FolderView(int Id, string Name, List<int> DashboardIds);

/// <summary>
/// Personal folders (User Portal): a user's own one-level groupings of dashboards. A dashboard can sit in several folders.
/// Only dashboards the user can open can be added, and one whose access is later revoked disappears from the folders
/// (the row stays, so it comes back if access is granted again). Folders are private and are not audited, like pins.
/// </summary>
public sealed class PersonalFolderService(AppDbContext db, TimeProvider clock)
{
    private IQueryable<int> OpenableDashboardIds(int userId) => db.GroupMembers
        .Where(m => m.UserId == userId && m.RemovedAtUtc == null && m.Group.Status == GroupStatus.Active && m.Group.Dashboard.Status == DashboardStatus.Active)
        .Select(m => m.DashboardId);

    public async Task<List<FolderView>> ListAsync(int userId, CancellationToken ct = default)
    {
        var folders = await db.PersonalFolders.AsNoTracking().Where(f => f.UserId == userId)
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Name).Select(f => new { f.Id, f.Name }).ToListAsync(ct);
        var open = OpenableDashboardIds(userId);
        var items = await db.PersonalFolderItems.AsNoTracking()
            .Where(i => i.Folder.UserId == userId && open.Contains(i.DashboardId))
            .OrderBy(i => i.AddedAtUtc).Select(i => new { i.FolderId, i.DashboardId }).ToListAsync(ct);
        var byFolder = items.ToLookup(i => i.FolderId, i => i.DashboardId);
        return folders.Select(f => new FolderView(f.Id, f.Name, byFolder[f.Id].Distinct().ToList())).ToList();
    }

    public async Task<FolderView> CreateAsync(int userId, string? name, CancellationToken ct = default)
    {
        var clean = await ValidNameAsync(userId, name, null, ct);
        if (await db.PersonalFolders.CountAsync(f => f.UserId == userId, ct) >= Rules.FoldersPerUserMax)
            throw new RuleException($"You can have up to {Rules.FoldersPerUserMax} folders. Delete one first.");
        var order = (await db.PersonalFolders.Where(f => f.UserId == userId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0) + 1;
        var folder = new PersonalFolder { UserId = userId, Name = clean, SortOrder = order, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.PersonalFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return new FolderView(folder.Id, folder.Name, []);
    }

    public async Task RenameAsync(int userId, int folderId, string? name, CancellationToken ct = default)
    {
        var folder = await OwnedAsync(userId, folderId, ct);
        folder.Name = await ValidNameAsync(userId, name, folderId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int userId, int folderId, CancellationToken ct = default)
    {
        await OwnedAsync(userId, folderId, ct);
        await db.PersonalFolderItems.Where(i => i.FolderId == folderId).ExecuteDeleteAsync(ct);
        await db.PersonalFolders.Where(f => f.Id == folderId && f.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public async Task AddDashboardAsync(int userId, int folderId, int dashboardId, CancellationToken ct = default)
    {
        await OwnedAsync(userId, folderId, ct);
        if (!await OpenableDashboardIds(userId).AnyAsync(id => id == dashboardId, ct))
            throw new RuleException("You can only add dashboards you can open.");
        if (await db.PersonalFolderItems.AnyAsync(i => i.FolderId == folderId && i.DashboardId == dashboardId, ct)) return;
        db.PersonalFolderItems.Add(new PersonalFolderItem { FolderId = folderId, DashboardId = dashboardId, AddedAtUtc = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveDashboardAsync(int userId, int folderId, int dashboardId, CancellationToken ct = default)
    {
        await OwnedAsync(userId, folderId, ct);
        await db.PersonalFolderItems.Where(i => i.FolderId == folderId && i.DashboardId == dashboardId).ExecuteDeleteAsync(ct);
    }

    private async Task<PersonalFolder> OwnedAsync(int userId, int folderId, CancellationToken ct) =>
        await db.PersonalFolders.SingleOrDefaultAsync(f => f.Id == folderId && f.UserId == userId, ct) ?? throw new KeyNotFoundException();

    private async Task<string> ValidNameAsync(int userId, string? name, int? exceptId, CancellationToken ct)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) throw new RuleException("Enter a name for the folder.");
        if (clean.Length > Rules.FolderNameMax) throw new RuleException($"A folder name can have up to {Rules.FolderNameMax} characters.");
        if (await db.PersonalFolders.AnyAsync(f => f.UserId == userId && f.Name == clean && f.Id != exceptId, ct))
            throw new RuleException("You already have a folder with that name.");
        return clean;
    }
}

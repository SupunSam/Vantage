using Microsoft.EntityFrameworkCore;
using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

public sealed record CategoryNode(int Id, string Name, int? ParentId, int Level, int SortOrder, string Path, int DashboardCount, int ChildCount);

/// <summary>
/// Category Master: a three-level tree (Primary, Secondary, Tertiary). Names are unique within their parent.
/// A category that still holds dashboards (any status, retired included) or sub-categories can't be deleted.
/// </summary>
public sealed class CategoryService(AppDbContext db, AuditWriter audit, TimeProvider clock)
{
    /// <summary>All categories in tree order (each parent followed by its children), with full paths and counts.</summary>
    public async Task<List<CategoryNode>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.ParentId, c.Level, c.SortOrder })
            .ToListAsync(ct);
        var dashboards = await db.Dashboards.AsNoTracking()
            .Where(d => d.CategoryId != null)
            .GroupBy(d => d.CategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var byParent = rows.ToLookup(r => r.ParentId);
        var result = new List<CategoryNode>();
        void Walk(int? parentId, string prefix)
        {
            foreach (var r in byParent[parentId].OrderBy(r => r.SortOrder).ThenBy(r => r.Name))
            {
                var path = prefix.Length == 0 ? r.Name : $"{prefix} / {r.Name}";
                result.Add(new CategoryNode(r.Id, r.Name, r.ParentId, r.Level, r.SortOrder, path,
                    dashboards.GetValueOrDefault(r.Id), byParent[r.Id].Count()));
                Walk(r.Id, path);
            }
        }
        Walk(null, "");
        return result;
    }

    /// <summary>Full path ("Finance / Payroll / Monthly") of every category, keyed by ID.</summary>
    public async Task<Dictionary<int, string>> PathsAsync(CancellationToken ct = default) =>
        (await ListAsync(ct)).ToDictionary(c => c.Id, c => c.Path);

    public async Task<Category> CreateAsync(string? name, int? parentId, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        var level = 1;
        if (parentId is { } pid)
        {
            var parent = await db.Categories.SingleOrDefaultAsync(c => c.Id == pid, ct) ?? throw new KeyNotFoundException();
            if (parent.Level >= Rules.CategoryLevels)
                throw new RuleException("Tertiary is the deepest level; a tertiary category can't have sub-categories.");
            level = parent.Level + 1;
        }
        await EnsureUniqueAsync(clean, parentId, null, ct);

        var sort = (await db.Categories.Where(c => c.ParentId == parentId).MaxAsync(c => (int?)c.SortOrder, ct) ?? 0) + 1;
        var category = new Category { Name = clean, ParentId = parentId, Level = level, SortOrder = sort, CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        audit.Add("category.created", "Category", category.Id, null, new { category.Name, parentId, level });
        await db.SaveChangesAsync(ct);
        return category;
    }

    public async Task<Category> RenameAsync(int id, string? name, CancellationToken ct = default)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        var clean = CleanName(name);
        if (clean == category.Name) return category;
        await EnsureUniqueAsync(clean, category.ParentId, id, ct);
        audit.Add("category.renamed", "Category", id, null, new { from = category.Name, to = clean });
        category.Name = clean;
        await db.SaveChangesAsync(ct);
        return category;
    }

    /// <summary>Moves a category one place up (-1) or down (+1) among its siblings.</summary>
    public async Task MoveAsync(int id, int direction, CancellationToken ct = default)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        var siblings = await db.Categories.Where(c => c.ParentId == category.ParentId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        var index = siblings.FindIndex(c => c.Id == id);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count) return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i + 1;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        var children = await db.Categories.CountAsync(c => c.ParentId == id, ct);
        if (children > 0)
            throw new RuleException($"'{category.Name}' has {children} sub-categor{(children == 1 ? "y" : "ies")}. Delete or move them first.");
        var dashboards = await db.Dashboards.CountAsync(d => d.CategoryId == id, ct);
        if (dashboards > 0)
            throw new RuleException($"'{category.Name}' holds {dashboards} dashboard{(dashboards == 1 ? "" : "s")} (retired ones included). Move them to another category in Dashboards Master first.");
        db.Categories.Remove(category);
        audit.Add("category.deleted", "Category", id, null, new { category.Name, category.Level });
        await db.SaveChangesAsync(ct);
    }

    private static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) throw new RuleException("Enter a category name.");
        if (clean.Length > Rules.CategoryNameMax) throw new RuleException($"Category names are up to {Rules.CategoryNameMax} characters.");
        if (clean.Contains('/')) throw new RuleException("Category names can't contain '/', which separates levels in a path.");
        return clean;
    }

    private async Task EnsureUniqueAsync(string name, int? parentId, int? exceptId, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(c => c.ParentId == parentId && c.Name == name && c.Id != exceptId, ct))
            throw new RuleException(parentId is null
                ? $"There is already a primary category named '{name}'."
                : $"'{name}' already exists under this category.");
    }
}

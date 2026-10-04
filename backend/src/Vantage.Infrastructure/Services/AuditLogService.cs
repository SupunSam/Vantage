using Microsoft.EntityFrameworkCore;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <param name="From">First day to include (date only).</param>
/// <param name="To">Last day to include (date only).</param>
/// <param name="Actor">Part of the actor's name or email.</param>
/// <param name="Search">Part of the entity id or the Details text.</param>
public sealed record AuditQuery(DateOnly? From, DateOnly? To, string? Actor, string? EntityType, string? Action, string? ServiceNowReference, string? Search, int? DashboardId, int Page = 1, int PageSize = 50);

public sealed record AuditRow(
    long Id, DateTime OccurredAtUtc, int? ActorUserId, string? Actor, string Action, string EntityType, string? EntityId,
    int? DashboardId, string? Dashboard, string? ServiceNowReference, string? IpAddress, string? CorrelationId, string? Details, string Description);

internal sealed record AuditProjection(AuditLog Log, string? Actor, string? Dashboard);

public sealed record AuditPage(int Total, int Page, int PageSize, DateTime SearchableFromUtc, IReadOnlyList<AuditRow> Rows);

/// <summary>
/// Reads the audit trail for the Admin Portal. The trail is kept permanently, but the viewer only searches the last
/// 12 months (requirements, section 13); older rows stay in the database.
/// </summary>
public sealed class AuditLogService(AppDbContext db, TimeProvider clock)
{
    public const int MaxPageSize = 200;
    public const int MaxExportRows = 50_000;
    public const int SearchableMonths = 12;

    private DateTime SearchableFrom => clock.GetUtcNow().UtcDateTime.AddMonths(-SearchableMonths);

    private IQueryable<AuditLog> Filter(AuditQuery q)
    {
        var from = SearchableFrom;
        if (q.From is { } f && f.ToDateTime(TimeOnly.MinValue) > from) from = f.ToDateTime(TimeOnly.MinValue);

        var rows = db.AuditLogs.AsNoTracking().Where(a => a.OccurredAtUtc >= from);
        if (q.To is { } t) { var end = t.AddDays(1).ToDateTime(TimeOnly.MinValue); rows = rows.Where(a => a.OccurredAtUtc < end); }
        if (!string.IsNullOrWhiteSpace(q.EntityType)) rows = rows.Where(a => a.EntityType == q.EntityType);
        if (!string.IsNullOrWhiteSpace(q.Action)) rows = rows.Where(a => a.Action == q.Action);
        if (q.DashboardId is { } d) rows = rows.Where(a => a.DashboardId == d);
        if (!string.IsNullOrWhiteSpace(q.ServiceNowReference))
        {
            var sn = q.ServiceNowReference.Trim();
            rows = rows.Where(a => a.ServiceNowReference != null && a.ServiceNowReference.Contains(sn));
        }
        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var who = q.Actor.Trim();
            var ids = db.Users.Where(u => u.Email.Contains(who) || (u.DisplayName != null && u.DisplayName.Contains(who))).Select(u => u.Id);
            rows = rows.Where(a => a.ActorUserId != null && ids.Contains(a.ActorUserId.Value));
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            rows = rows.Where(a => (a.EntityId != null && a.EntityId.Contains(s)) || (a.Details != null && a.Details.Contains(s)));
        }

        return rows;
    }

    /// <summary>Adds the actor's and dashboard's names. Call after ordering and paging.</summary>
    private IQueryable<AuditProjection> WithNames(IQueryable<AuditLog> rows) => rows.Select(a => new AuditProjection(
        a,
        db.Users.Where(u => u.Id == a.ActorUserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
        db.Dashboards.Where(x => x.Id == a.DashboardId).Select(x => x.Name).FirstOrDefault()));

    private static IOrderedQueryable<AuditLog> Newest(IQueryable<AuditLog> rows) => rows.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id);

    /// <summary>Names of the things the rows are about (users by email, groups, roles, categories, tenants), one query per type.</summary>
    private async Task<Func<AuditLog, string?>> SubjectsAsync(IReadOnlyList<AuditProjection> rows, CancellationToken ct)
    {
        List<int> Ids(string type) => rows.Where(r => r.Log.EntityType == type && int.TryParse(r.Log.EntityId, out _)).Select(r => int.Parse(r.Log.EntityId!)).Distinct().ToList();
        var users = Ids("User"); var groups = Ids("DashboardGroup"); var roles = Ids("Role"); var categories = Ids("Category"); var tenants = Ids("BiTenant");
        var names = new Dictionary<(string, int), string>();
        foreach (var x in await db.Users.AsNoTracking().Where(x => users.Contains(x.Id)).Select(x => new { x.Id, x.Email }).ToListAsync(ct)) names[("User", x.Id)] = x.Email;
        foreach (var x in await db.DashboardGroups.AsNoTracking().Where(x => groups.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) names[("DashboardGroup", x.Id)] = x.Name;
        foreach (var x in await db.Roles.AsNoTracking().Where(x => roles.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) names[("Role", x.Id)] = x.Name;
        foreach (var x in await db.Categories.AsNoTracking().Where(x => categories.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) names[("Category", x.Id)] = x.Name;
        foreach (var x in await db.BiTenants.AsNoTracking().Where(x => tenants.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)) names[("BiTenant", x.Id)] = x.Name;
        return log => int.TryParse(log.EntityId, out var id) && names.TryGetValue((log.EntityType, id), out var n) ? n : null;
    }

    private static AuditRow ToRow(AuditProjection p, Func<AuditLog, string?> subject, bool withDetails) => new(
        p.Log.Id, p.Log.OccurredAtUtc, p.Log.ActorUserId, p.Actor, p.Log.Action, p.Log.EntityType, p.Log.EntityId,
        p.Log.DashboardId, p.Dashboard, p.Log.ServiceNowReference, p.Log.IpAddress, p.Log.CorrelationId, withDetails ? p.Log.Details : null,
        AuditDescriber.Describe(p.Log.Action, p.Log.EntityType, p.Log.EntityId, p.Log.Details, subject(p.Log), p.Dashboard, p.Log.ServiceNowReference));

    public async Task<AuditPage> SearchAsync(AuditQuery q, CancellationToken ct)
    {
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, q.Page);
        var query = Filter(q);
        var total = await query.CountAsync(ct);
        var rows = await WithNames(Newest(query).Skip((page - 1) * size).Take(size)).ToListAsync(ct);
        var subject = await SubjectsAsync(rows, ct);
        return new AuditPage(total, page, size, SearchableFrom, rows.Select(p => ToRow(p, subject, false)).ToList());
    }

    /// <summary>One entry with its full Details JSON (before and after values).</summary>
    public async Task<AuditRow> GetAsync(long id, CancellationToken ct)
    {
        var p = await WithNames(db.AuditLogs.AsNoTracking().Where(a => a.Id == id)).FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException();
        return ToRow(p, await SubjectsAsync([p], ct), true);
    }

    /// <summary>The same filter as the grid, newest first, with Details, capped at <see cref="MaxExportRows"/>.</summary>
    public async Task<(IReadOnlyList<AuditRow> Rows, bool Truncated)> ExportAsync(AuditQuery q, CancellationToken ct)
    {
        var rows = await WithNames(Newest(Filter(q)).Take(MaxExportRows + 1)).ToListAsync(ct);
        var subject = await SubjectsAsync(rows, ct);
        return (rows.Take(MaxExportRows).Select(p => ToRow(p, subject, true)).ToList(), rows.Count > MaxExportRows);
    }

    public async Task<(IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Actions)> OptionsAsync(CancellationToken ct)
    {
        var from = SearchableFrom;
        var rows = db.AuditLogs.AsNoTracking().Where(a => a.OccurredAtUtc >= from);
        return (await rows.Select(a => a.EntityType).Distinct().OrderBy(x => x).ToListAsync(ct),
                await rows.Select(a => a.Action).Distinct().OrderBy(x => x).ToListAsync(ct));
    }
}

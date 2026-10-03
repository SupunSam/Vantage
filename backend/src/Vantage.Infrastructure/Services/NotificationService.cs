using Microsoft.EntityFrameworkCore;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>
/// In-portal notifications (the bell in the top bar). Links are portal-relative ("/dashboards/12") and work in both
/// portals. Email through the outbox is added with access requests.
/// </summary>
public sealed class NotificationService(AppDbContext db, TimeProvider clock)
{
    /// <summary>Adds one notification per user to the current unit of work; saved with the caller's SaveChanges.</summary>
    public void Notify(IEnumerable<int> userIds, string type, string title, string? body = null, string? link = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var userId in userIds.Distinct())
            db.Notifications.Add(new Notification { UserId = userId, Type = type, Title = Clip(title, 200)!, Body = Clip(body, 1000), Link = link, CreatedAtUtc = now });
    }

    public async Task<(List<Notification> Items, int Unread)> ListAsync(int userId, int take = 20, CancellationToken ct = default)
    {
        var items = await db.Notifications.AsNoTracking().Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Take(take).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, ct);
        return (items, unread);
    }

    public async Task MarkReadAsync(int userId, long? id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Notifications.Where(n => n.UserId == userId && n.ReadAtUtc == null && (id == null || n.Id == id))
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAtUtc, now), ct);
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

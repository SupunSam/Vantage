using System.Text.Json;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Services;

/// <summary>Context of the current request, filled in by the API layer.</summary>
public interface IRequestContext
{
    int? UserId { get; }
    string? CorrelationId { get; }
    string? IpAddress { get; }
}

public sealed class AuditWriter(AppDbContext db, IRequestContext ctx, TimeProvider clock)
{
    /// <summary>Adds an audit row to the current unit of work; saved with the caller's SaveChanges.</summary>
    /// <param name="serviceNowReference">The ServiceNow ticket the change was made under, if the admin entered one.</param>
    public void Add(string action, string entityType, object? entityId, int? dashboardId = null, object? details = null, string? serviceNowReference = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime,
            ActorUserId = ctx.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            DashboardId = dashboardId,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            CorrelationId = ctx.CorrelationId,
            IpAddress = ctx.IpAddress,
            ServiceNowReference = string.IsNullOrWhiteSpace(serviceNowReference) ? null : serviceNowReference.Trim()[..Math.Min(64, serviceNowReference.Trim().Length)],
        });
    }
}

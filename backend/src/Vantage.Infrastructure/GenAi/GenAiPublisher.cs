using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

namespace Vantage.Infrastructure.GenAi;

public sealed record PublishGenAiRequest(
    string Name, string Code, string? Description, int PrimaryOwnerId, int? BackupOwnerId, int? CategoryId, string FileName,
    IReadOnlyList<string>? Tags = null, Audience Audience = Audience.Internal, DataClassification DataClassification = DataClassification.Internal);

/// <summary>
/// Publishes a GenAI dashboard: checks the HTML, creates the dashboard with its default group (the owners), keeps the
/// file as version 1 and makes it Active. There is no import step, so it is live as soon as this returns. Nothing is
/// created when the file fails its checks.
/// </summary>
public sealed class GenAiPublisher(
    AppDbContext db, DashboardFactory factory, DashboardMasterService master, GenAiService genAi, AuditWriter audit,
    NotificationService notifications, TimeProvider clock)
{
    public async Task<PublishStatus> PublishAsync(PublishGenAiRequest req, Stream html, int actorUserId, CancellationToken ct = default)
    {
        var (bytes, result) = await genAi.ValidateUploadAsync(html, req.FileName, ct);
        await master.ValidateCategoryAsync(req.CategoryId, ct);
        var tags = DashboardMasterService.ValidateTags(req.Tags);
        await master.ValidateOwnersAsync(req.PrimaryOwnerId, req.BackupOwnerId, ct);

        var dashboard = await factory.CreateAsync(new Dashboard
        {
            Code = req.Code.Trim(), Name = req.Name.Trim(), Description = req.Description?.Trim(),
            Type = DashboardType.GenAi, Status = DashboardStatus.Publishing, CategoryId = req.CategoryId, RlsEnabled = false,
            PrimaryOwnerId = req.PrimaryOwnerId, BackupOwnerId = req.BackupOwnerId == req.PrimaryOwnerId ? null : req.BackupOwnerId,
            Audience = req.Audience, DataClassification = req.DataClassification,
            Tags = tags.Select(t => new DashboardTag { Tag = t }).ToList(),
        }, null, actorUserId, ct);

        var version = await genAi.AddVersionAsync(dashboard, bytes, req.FileName, actorUserId, null, ct);
        dashboard.Status = DashboardStatus.Active;
        dashboard.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Add("dashboard.published", "Dashboard", dashboard.Id, dashboard.Id,
            new { type = "GenAi", version, file = Path.GetFileName(req.FileName), size = bytes.Length, libraries = result.Libraries });
        notifications.Notify(new[] { dashboard.PrimaryOwnerId, dashboard.BackupOwnerId, dashboard.CreatedByUserId }.OfType<int>().Distinct(),
            "publish.succeeded", $"{dashboard.Name} is published", "It's live for the members of its groups.", $"/dashboards/{dashboard.Id}");
        await db.SaveChangesAsync(ct);

        var group = db.DashboardGroups.Where(g => g.DashboardId == dashboard.Id && g.IsDefault).Select(g => g.Name).Single();
        return new PublishStatus(dashboard.Id, dashboard.Name, "Active", null, null, group, result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null);
    }
}

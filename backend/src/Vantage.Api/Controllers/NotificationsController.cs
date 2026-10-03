using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>The signed-in user's notifications, for the bell in the top bar of both portals.</summary>
[ApiController, Authorize, Route("api/notifications")]
public sealed class NotificationsController(CurrentUser current, NotificationService notifications) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (current.UserId is not { } userId || await current.GetAsync(ct) is null) return Unauthorized();
        var (items, unread) = await notifications.ListAsync(userId, 20, ct);
        return Ok(new
        {
            unread,
            items = items.Select(n => new { n.Id, n.Type, n.Title, n.Body, n.Link, n.CreatedAtUtc, read = n.ReadAtUtc != null }),
        });
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> Read(long id, CancellationToken ct)
    {
        if (current.UserId is not { } userId) return Unauthorized();
        await notifications.MarkReadAsync(userId, id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        if (current.UserId is not { } userId) return Unauthorized();
        await notifications.MarkReadAsync(userId, null, ct);
        return NoContent();
    }
}

using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Jobs;

namespace Vantage.Api.Controllers;

/// <summary>Scheduled Jobs page: each job's schedule, last and next run, pause and resume, Run Now and the run history.</summary>
[Route("api/admin/jobs")]
public sealed class JobsController(CurrentUser current, JobRunner jobs) : AdminControllerBase(current)
{
    public sealed record PausedBody(bool Paused);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await RequireAsync(AppModules.ScheduledJobs, PermissionLevel.View, ct) is { } denied) return denied;
        return Ok(await jobs.ListAsync(ct));
    }

    [HttpGet("runs")]
    public async Task<IActionResult> Runs(string? job, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        if (await RequireAsync(AppModules.ScheduledJobs, PermissionLevel.View, ct) is { } denied) return denied;
        var (items, total) = await jobs.HistoryAsync(job, page, Math.Clamp(pageSize, 5, 100), ct);
        return Ok(new { items, total });
    }

    [HttpPost("{name}/run")]
    public async Task<IActionResult> Run(string name, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.ScheduledJobs, PermissionLevel.Edit, ct) is { } denied) return denied;
        var me = await Current.GetAsync(ct);
        return await Guard(async () => Ok((await jobs.RunNowAsync(name, me!.Id, ct)).Run));
    }

    [HttpPut("{name}/paused")]
    public async Task<IActionResult> SetPaused(string name, PausedBody body, CancellationToken ct)
    {
        if (await RequireAsync(AppModules.ScheduledJobs, PermissionLevel.Edit, ct) is { } denied) return denied;
        return await Guard(async () =>
        {
            await jobs.SetPausedAsync(name, body.Paused, ct);
            return NoContent();
        });
    }
}

using Microsoft.AspNetCore.Mvc;
using Vantage.Api.Auth;
using Vantage.Domain;
using Vantage.Infrastructure.Services;

namespace Vantage.Api.Controllers;

/// <summary>The Admin Portal's Home page. Open to anyone who can sign in to the Admin Portal; each part is filled only if their role may see it.</summary>
[Route("api/admin/home")]
public sealed class HomeController(CurrentUser current, AdminHomeService home) : AdminControllerBase(current)
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var me = await Current.GetAsync(ct);
        if (me is null) return Unauthorized();
        var scope = new HomeScope(
            Dashboards: me.Can(AppModules.DashboardConfig, PermissionLevel.View), Users: me.Can(AppModules.Users, PermissionLevel.View),
            Groups: me.Can(AppModules.Groups, PermissionLevel.View), Analytics: me.Can(AppModules.Analytics, PermissionLevel.View),
            Audit: me.Can(AppModules.Audit, PermissionLevel.View), Jobs: me.Can(AppModules.ScheduledJobs, PermissionLevel.View),
            Tenants: me.Can(AppModules.Tenants, PermissionLevel.View));
        return Ok(await home.GetAsync(scope, ct));
    }
}

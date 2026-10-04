using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vantage.Infrastructure.GenAi;

namespace Vantage.Api.Controllers;

/// <summary>
/// Serves GenAI dashboard files. Reached only through the separate GenAI origin (its own nginx listener or, in AWS,
/// its own domain), never through the portals' /api. There is no sign-in here: the signed link in the path is the
/// credential, it is issued after the access check, and it expires. Every response carries the strict policy and sandbox.
/// </summary>
[ApiController, AllowAnonymous, Route("genai")]
public sealed class GenAiContentController(GenAiService genAi) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var content = await genAi.OpenAsync(token, ct);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store";
        if (content is null)
        {
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return NotFound("This link has expired or the dashboard isn't available. Go back to the portal and open it again.");
        }
        Response.Headers.ContentSecurityPolicy = content.ContentSecurityPolicy;
        return File(content.Content, "text/html; charset=utf-8");
    }
}

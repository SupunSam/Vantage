using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vantage.Domain;
using Vantage.Infrastructure.Data;

namespace Vantage.Api.Auth;

/// <summary>
/// DEVELOPMENT ONLY. Stands in for Cognito/ADFS: the portals send the chosen user's email in the
/// X-Dev-User header. Registered only when ASPNETCORE_ENVIRONMENT=Development.
/// </summary>
public sealed class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevUser";
    public const string Header = "X-Dev-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
            return AuthenticateResult.NoResult();

        var email = values.ToString().Trim();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.Email, u.Status })
            .SingleOrDefaultAsync(Context.RequestAborted);

        if (user is null) return AuthenticateResult.Fail($"Unknown dev user '{email}'.");
        if (user.Status == UserStatus.Inactive) return AuthenticateResult.Fail("User is inactive.");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Secrets;

namespace Vantage.Infrastructure.Embedding;

/// <summary>
/// Signs Tableau Connected App (direct trust) JWTs for the Embedding API v3.
/// Claims follow Tableau's documentation: iss = client ID, kid = secret ID, sub = Tableau user name,
/// aud = "tableau", scp = ["tableau:views:embed"], exp at most 10 minutes ahead.
/// </summary>
public sealed class TableauTokenService(ISecretStore secrets, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public async Task<(string Token, DateTimeOffset ExpiresAt)> CreateTokenAsync(BiTenant tenant, string tableauUserName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.ConnectedAppClientId) || string.IsNullOrWhiteSpace(tenant.ConnectedAppSecretId) || string.IsNullOrWhiteSpace(tenant.SecretName))
            throw new EmbedException("not-configured", $"Tableau tenant '{tenant.Name}' is missing its Connected App client ID, secret ID or secret name.");
        if (string.IsNullOrWhiteSpace(tableauUserName))
            throw new EmbedException("tableau-user-missing", "Your profile has no Tableau user name. Ask a Super Admin to add it.");

        var secretValue = await secrets.GetAsync(tenant.SecretName, ct)
            ?? throw new EmbedException("secret-missing", $"Tableau Connected App secret '{tenant.SecretName}' is not set.");

        return Sign(tenant.ConnectedAppClientId, tenant.ConnectedAppSecretId, secretValue, tableauUserName, clock.GetUtcNow());
    }

    public static (string Token, DateTimeOffset ExpiresAt) Sign(string clientId, string secretId, string secretValue, string userName, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretValue)) { KeyId = secretId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = now.Add(Lifetime);

        var header = new JwtHeader(credentials) { ["iss"] = clientId };
        var payload = new JwtPayload
        {
            { "iss", clientId },
            { "sub", userName },
            { "aud", "tableau" },
            { "jti", Guid.NewGuid().ToString() },
            { "iat", now.ToUnixTimeSeconds() },
            { "exp", expires.ToUnixTimeSeconds() },
            { "scp", new[] { "tableau:views:embed" } },
        };

        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        return (token, expires);
    }

    /// <summary>Embedding API v3 script hosted by the Tableau Server itself (Tableau Public uses <see cref="TableauViewUrl.ScriptUrl"/>).</summary>
    public static string ScriptUrl(BiTenant tenant) =>
        $"{tenant.ServerUrl?.TrimEnd('/')}/javascripts/api/tableau.embedding.3.latest.min.js";
}

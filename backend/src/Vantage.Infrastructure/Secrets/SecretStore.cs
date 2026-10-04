using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Data;

namespace Vantage.Infrastructure.Secrets;

/// <summary>
/// Secrets referenced by name from the tenant master (Power BI client secrets, Tableau Connected App secrets).
/// They are entered in the Admin Portal and can be replaced but never read back through the UI.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string secretName, CancellationToken ct = default);
    Task SetAsync(string secretName, string value, int? updatedByUserId, CancellationToken ct = default);
    Task<DateTime?> GetUpdatedAtAsync(string secretName, CancellationToken ct = default);
}

/// <summary>
/// Local and Docker builds: secrets are encrypted with ASP.NET Core Data Protection (keys kept in a Docker volume)
/// and stored in app.AppSecrets. In AWS this is replaced by an AWS Secrets Manager implementation.
/// </summary>
public sealed class DbSecretStore(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock) : ISecretStore
{
    // Keep this purpose string as it is: changing it makes stored secrets unreadable.
    private readonly IDataProtector _protector = protection.CreateProtector("Vantage.Secrets.v1");

    public async Task<string?> GetAsync(string secretName, CancellationToken ct = default)
    {
        var row = await db.AppSecrets.AsNoTracking().SingleOrDefaultAsync(s => s.Name == secretName, ct);
        if (row is null) return null;
        try
        {
            return _protector.Unprotect(row.ProtectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The encryption keys were lost or changed (e.g. the keys volume was deleted): treat as not set so the UI asks again.
            return null;
        }
    }

    public async Task SetAsync(string secretName, string value, int? updatedByUserId, CancellationToken ct = default)
    {
        var row = await db.AppSecrets.SingleOrDefaultAsync(s => s.Name == secretName, ct);
        if (row is null)
        {
            row = new AppSecret { Name = secretName };
            db.AppSecrets.Add(row);
        }
        row.ProtectedValue = _protector.Protect(value.Trim());
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.UpdatedByUserId = updatedByUserId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateTime?> GetUpdatedAtAsync(string secretName, CancellationToken ct = default) =>
        await db.AppSecrets.AsNoTracking().Where(s => s.Name == secretName).Select(s => (DateTime?)s.UpdatedAtUtc).SingleOrDefaultAsync(ct);
}

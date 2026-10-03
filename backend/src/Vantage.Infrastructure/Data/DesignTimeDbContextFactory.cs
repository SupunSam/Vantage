using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vantage.Infrastructure.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without the API's secrets.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("RD_DESIGN_CONNECTION")
                 ?? "Server=localhost,1433;Database=RDDashboard;Integrated Security=false;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "app"))
            .Options;
        return new AppDbContext(options);
    }
}

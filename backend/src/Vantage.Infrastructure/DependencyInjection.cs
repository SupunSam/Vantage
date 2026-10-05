using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Email;
using Vantage.Infrastructure.Embedding;
using Vantage.Infrastructure.Jobs;
using Vantage.Infrastructure.GenAi;
using Vantage.Infrastructure.Secrets;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Storage;
using Vantage.Infrastructure.Tenants;
using Microsoft.AspNetCore.DataProtection;

namespace Vantage.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not set. In Docker it comes from docker-compose.yml and .env.");

        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure(3);
            sql.MigrationsHistoryTable("__EFMigrationsHistory", "app");
        }));

        services.AddSingleton(TimeProvider.System);
        // Secrets are encrypted with Data Protection; keys persist in DataProtection:KeysPath (a Docker volume).
        // Keep this application name: changing it makes stored tenant secrets unreadable.
        var dp = services.AddDataProtection().SetApplicationName("Vantage");
        if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        services.AddScoped<ISecretStore, DbSecretStore>();
        services.AddScoped<IPowerBiTokenProvider, MsalPowerBiTokenProvider>();
        services.AddHttpClient<TenantVerifier>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<RoleService>();
        services.AddScoped<UserService>();
        services.AddScoped<HrmsSyncService>();
        // Uploaded files: Storage:LocalPath (a Docker volume); S3 in AWS.
        // Generous timeout: .pbix uploads can be large.
        services.AddHttpClient<PowerBiClient>(c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IFileStore>(_ => new LocalFileStore(config["Storage:LocalPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vantage", "files")));
        services.AddScoped<PublishingService>();
        services.AddScoped<TableauTokenService>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<OwnershipService>();
        services.AddScoped<OwnerDepartureService>();
        services.AddScoped<DashboardFactory>();
        services.Configure<GenAiOptions>(config.GetSection("GenAi"));
        services.AddScoped<GenAiSettings>();
        services.AddScoped<IFileScanner, ConfiguredFileScanner>();
        services.AddScoped<GenAiService>();
        services.AddScoped<GenAiPublisher>();
        services.AddScoped<TableauPublisher>();
        services.AddScoped<EmbedService>();
        services.AddScoped<CategoryService>();
        services.AddScoped<DashboardMasterService>();
        services.AddScoped<GroupService>();
        services.AddScoped<AccessGroupService>();
        services.AddScoped<DashboardVersionService>();
        services.AddScoped<AccessRequestService>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<GroupAddRequestService>();
        services.AddScoped<AccessGroupRuleService>();
        services.AddScoped<ConfigService>();
        services.AddScoped<AnalyticsService>();
        services.AddScoped<AdminHomeService>();
        services.AddScoped<PersonalFolderService>();
        // Email: queued in the outbox table and sent by a background worker (Mailpit locally, SES in AWS).
        services.Configure<EmailOptions>(config.GetSection("Email"));
        services.AddScoped<EmailOutboxService>();
        services.AddHostedService<EmailSenderWorker>();
        services.AddScoped<NotificationService>();
        // Scheduled jobs: the runner decides when each runs and records every run; the scheduler wakes it every 30 seconds.
        services.AddScoped<IScheduledJob, HrmsSyncJob>();
        services.AddScoped<IScheduledJob, InactivityJob>();
        services.AddScoped<IScheduledJob, NewHireDigestJob>();
        services.AddScoped<JobRunner>();
        services.AddHostedService<JobScheduler>();
        services.AddScoped<DbSeeder>();
        return services;
    }
}

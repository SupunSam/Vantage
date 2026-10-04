using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vantage.Api.Auth;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Jobs;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>The three jobs and the runner must resolve from the container the API builds, or the scheduler fails at its first tick.</summary>
public class ScheduledJobWiringTests
{
    [Fact]
    public void The_runner_and_all_three_jobs_resolve_and_each_has_a_distinct_name()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Server=none;Database=none;" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddInfrastructure(config);
        services.AddScoped<CurrentUser>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<CurrentUser>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<JobRunner>());
        var names = scope.ServiceProvider.GetServices<IScheduledJob>().Select(j => j.Name).Order().ToList();
        Assert.Equal(["hrms-sync", "inactivity-check", "new-hire-digest"], names);
        Assert.Contains(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), h => h is JobScheduler);
    }
}

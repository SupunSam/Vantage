using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Vantage.Infrastructure.Jobs;

/// <summary>
/// Wakes every 30 seconds and runs whichever scheduled jobs are due (see <see cref="JobRunner"/>). Set Jobs:Enabled to false to switch
/// the scheduler off on an instance; Run Now still works. Safe on several API instances: a run is claimed with one atomic update.
/// </summary>
public sealed class JobScheduler(IServiceScopeFactory scopes, IConfiguration config, ILogger<JobScheduler> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Jobs:Enabled", true))
        {
            log.LogInformation("The job scheduler is switched off (Jobs:Enabled = false); jobs only run with Run Now.");
            return;
        }
        // Let the API finish starting (migrations run first) before the first look at the schedule.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var ran = await scope.ServiceProvider.GetRequiredService<JobRunner>().RunDueAsync(stoppingToken);
                if (ran > 0) log.LogInformation("Ran {Count} scheduled job(s)", ran);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "The job scheduler's check failed"); }
            try { await Task.Delay(Tick, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}

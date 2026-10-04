using Vantage.Domain;
using Vantage.Domain.Entities;
using Vantage.Infrastructure.Jobs;
using Vantage.Infrastructure.Services;
using Vantage.Infrastructure.Settings;

namespace Vantage.Tests;

/// <summary>The cron parser and the lifecycle rules are pure code, so these run everywhere (no SQL Server needed).</summary>
public class CronScheduleTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void The_default_monthly_schedule_runs_at_two_in_the_morning_on_the_first()
    {
        var cron = CronSchedule.Parse("0 2 1 * *");
        Assert.Equal(Utc(2026, 11, 1, 2), cron.Next(Utc(2026, 10, 4, 5, 9, 37)));
        Assert.Equal(Utc(2026, 12, 1, 2), cron.Next(Utc(2026, 11, 1, 2)));            // strictly after the run that just happened
        Assert.Equal(Utc(2026, 11, 1, 2), cron.Next(Utc(2026, 11, 1, 1, 59, 59)));
        Assert.Equal(Utc(2027, 1, 1, 2), cron.Next(Utc(2026, 12, 15)));                // across the new year
    }

    [Fact]
    public void Steps_lists_and_ranges_work()
    {
        Assert.Equal(Utc(2026, 10, 4, 10, 15), CronSchedule.Parse("*/15 * * * *").Next(Utc(2026, 10, 4, 10, 1)));
        Assert.Equal(Utc(2026, 10, 4, 12, 0), CronSchedule.Parse("0 6,12,18 * * *").Next(Utc(2026, 10, 4, 6, 0)));
        Assert.Equal(Utc(2026, 10, 7, 9, 30), CronSchedule.Parse("30 9 * * 1-5").Next(Utc(2026, 10, 6, 10)));   // Tue 10:00 -> Wed 09:30
        Assert.Equal(Utc(2026, 10, 4, 0, 50), CronSchedule.Parse("10-50/20 * * * *").Next(Utc(2026, 10, 4, 0, 30)));   // :10, :30, :50
    }

    [Fact]
    public void Day_of_week_counts_from_sunday_and_seven_is_also_sunday()
    {
        Assert.Equal(DayOfWeek.Sunday, Utc(2026, 10, 4).DayOfWeek);
        Assert.Equal(Utc(2026, 10, 5, 6), CronSchedule.Parse("0 6 * * 1").Next(Utc(2026, 10, 4, 6)));           // Monday after Sunday
        Assert.Equal(Utc(2026, 10, 11, 8), CronSchedule.Parse("0 8 * * 0").Next(Utc(2026, 10, 4, 8)));
        Assert.Equal(Utc(2026, 10, 11, 8), CronSchedule.Parse("0 8 * * 7").Next(Utc(2026, 10, 4, 8)));
    }

    [Fact]
    public void When_both_the_day_of_month_and_the_weekday_are_set_either_one_runs()
    {
        var cron = CronSchedule.Parse("0 0 13 * 5");                                   // the 13th, or any Friday
        Assert.Equal(Utc(2026, 10, 9), cron.Next(Utc(2026, 10, 4)));                    // Friday 9 Oct comes before the 13th
        Assert.Equal(Utc(2026, 10, 13), cron.Next(Utc(2026, 10, 9)));
    }

    [Fact]
    public void A_date_that_never_exists_has_no_next_run_and_leap_days_are_found()
    {
        Assert.Null(CronSchedule.Parse("0 0 31 2 *").Next(Utc(2026, 1, 1)));
        Assert.Equal(Utc(2028, 2, 29), CronSchedule.Parse("0 0 29 2 *").Next(Utc(2026, 10, 4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("every day")]
    [InlineData("0 2 1 *")]               // four fields
    [InlineData("0 2 1 * * *")]           // six fields
    [InlineData("60 2 1 * *")]            // minute out of range
    [InlineData("0 24 1 * *")]
    [InlineData("0 2 0 * *")]
    [InlineData("0 2 1 13 *")]
    [InlineData("0 2 1 * 8")]
    [InlineData("5-1 * * * *")]           // backwards range
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    public void Anything_that_is_not_a_schedule_is_refused_with_a_reason(string text)
    {
        Assert.False(CronSchedule.TryParse(text, out var schedule, out var error));
        Assert.Null(schedule);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Throws<FormatException>(() => CronSchedule.Parse(text));
    }

    [Fact]
    public void The_shortest_gap_shows_how_often_a_schedule_really_fires()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CronSchedule.Parse("*/5 * * * *").ShortestGap(Utc(2026, 10, 4)));
        Assert.Equal(TimeSpan.FromDays(1), CronSchedule.Parse("0 3 * * *").ShortestGap(Utc(2026, 10, 4)));
        Assert.True(CronSchedule.Parse("0 2 1 * *").ShortestGap(Utc(2026, 10, 4)) >= TimeSpan.FromDays(28));
        Assert.Equal(TimeSpan.FromMinutes(10), CronSchedule.Parse("0,10 2 1 * *").ShortestGap(Utc(2026, 10, 4)));
    }
}

public class JobRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 3, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Weekly", "0 6 * * 1")]
    [InlineData("weekly", "0 6 * * 1")]
    [InlineData("Monthly", "0 6 1 * *")]
    [InlineData("Never", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_digest_frequency_picks_the_schedule(string? frequency, string? cron) => Assert.Equal(cron, JobRules.DigestCron(frequency));

    [Fact]
    public void The_digest_looks_back_a_week_or_a_month_and_every_digest_cron_is_a_valid_schedule()
    {
        var today = new DateOnly(2026, 10, 5);
        Assert.Equal(new DateOnly(2026, 9, 28), JobRules.NewHireWindowStart("Weekly", today));
        Assert.Equal(new DateOnly(2026, 9, 4), JobRules.NewHireWindowStart("Monthly", today));
        foreach (var cron in new[] { JobRules.WeeklyDigestCron, JobRules.MonthlyDigestCron, JobRules.InactivityCron }) Assert.NotNull(CronSchedule.Parse(cron).Next(Now));
    }

    [Fact]
    public void A_dashboard_is_inactive_once_the_last_view_is_the_threshold_old()
    {
        Assert.True(JobRules.IsInactive(Now.AddDays(-91), Now.AddDays(-200), Now.AddDays(-200), Now, 90, false));
        Assert.True(JobRules.IsInactive(Now.AddDays(-90), null, Now.AddDays(-200), Now, 90, false));          // exactly 90 days counts
        Assert.False(JobRules.IsInactive(Now.AddDays(-89), null, Now.AddDays(-200), Now, 90, false));
        Assert.False(JobRules.IsInactive(Now.AddDays(-400), null, Now.AddDays(-400), Now, 90, alreadyFlagged: true)); // told once
    }

    [Fact]
    public void A_dashboard_nobody_has_opened_counts_from_when_it_was_published()
    {
        Assert.False(JobRules.IsInactive(null, Now.AddDays(-10), Now.AddDays(-300), Now, 90, false));         // published recently: full period
        Assert.True(JobRules.IsInactive(null, Now.AddDays(-100), Now.AddDays(-300), Now, 90, false));
        Assert.True(JobRules.IsInactive(null, null, Now.AddDays(-100), Now, 90, false));                      // falls back to when it was created
        Assert.Equal(100, JobRules.DaysQuiet(null, Now.AddDays(-100), Now.AddDays(-300), Now));
    }

    [Fact]
    public void The_newest_view_wins_whichever_record_holds_it()
    {
        var old = Now.AddDays(-100);
        var recent = Now.AddDays(-2);
        Assert.Equal(recent, JobRules.LatestView(old, recent));
        Assert.Equal(recent, JobRules.LatestView(recent, old));
        Assert.Equal(old, JobRules.LatestView(old, null));
        Assert.Equal(recent, JobRules.LatestView(null, recent));
        Assert.Null(JobRules.LatestView(null, null));
    }

    [Fact]
    public void Only_a_non_active_hrms_status_means_someone_has_left()
    {
        Assert.False(JobRules.HasLeft(null));
        Assert.False(JobRules.HasLeft(new HrmsProfile { EmploymentStatus = "Active" }));
        Assert.True(JobRules.HasLeft(new HrmsProfile { EmploymentStatus = "Inactive" }));
    }

    [Theory]
    [InlineData(1, "1 dashboard")]
    [InlineData(0, "0 dashboards")]
    [InlineData(3, "3 dashboards")]
    public void Plurals_read_naturally(int n, string text) => Assert.Equal(text, JobRules.Plural(n, "dashboard"));
}

public class JobSettingsValidationTests
{
    [Theory]
    [InlineData("0 2 1 * *")]
    [InlineData("30 1 * * 0")]
    [InlineData("0 */6 * * *")]
    public void Good_hrms_schedules_are_accepted(string text) => Assert.Equal(text, ConfigService.NormaliseValue(SettingKeys.HrmsSyncCron, text));

    [Theory]
    [InlineData("* * * * *")]             // every minute
    [InlineData("*/10 * * * *")]
    [InlineData("99 2 1 * *")]            // not a real minute
    [InlineData("0 2 31 2 *")]            // 31 February never comes
    [InlineData("0 2 1 *")]
    public void Bad_or_too_frequent_hrms_schedules_are_refused(string text) =>
        Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(SettingKeys.HrmsSyncCron, text));

    [Fact]
    public void The_inactivity_threshold_takes_one_to_730_days_and_the_digest_takes_its_three_choices()
    {
        Assert.Equal("1", ConfigService.NormaliseValue(SettingKeys.InactivityDays, "1"));
        Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(SettingKeys.InactivityDays, "0"));
        Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(SettingKeys.InactivityDays, "731"));
        Assert.Equal("Weekly", ConfigService.NormaliseValue(SettingKeys.NewHireDigestFrequency, "weekly"));
        Assert.Throws<RuleException>(() => ConfigService.NormaliseValue(SettingKeys.NewHireDigestFrequency, "Daily"));
    }
}

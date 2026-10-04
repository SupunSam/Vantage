namespace Vantage.Infrastructure.Jobs;

/// <summary>
/// A standard five-field cron schedule (minute, hour, day of month, month, day of week), always in UTC.
/// Supports *, numbers, ranges (1-5), lists (1,15) and steps (*/10, 0-30/5). Day of week is 0 to 7 (0 and 7 are Sunday).
/// As in classic cron, when both day of month and day of week are restricted, a day matching either one runs.
/// Pure code with no database, so it is unit tested directly.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private bool _dayStar, _weekdayStar;

    private CronSchedule() { }

    public static CronSchedule Parse(string? text) =>
        TryParse(text, out var s, out var error) ? s! : throw new FormatException(error);

    public static bool TryParse(string? text, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5) { error = "A schedule needs five fields: minute, hour, day of month, month, day of week."; return false; }
        var s = new CronSchedule { _dayStar = parts[2].StartsWith('*'), _weekdayStar = parts[4].StartsWith('*') };
        string? bad =
            Fill(parts[0], 0, 59, s._minutes, "minute")
            ?? Fill(parts[1], 0, 23, s._hours, "hour")
            ?? Fill(parts[2], 1, 31, s._days, "day of month")
            ?? Fill(parts[3], 1, 12, s._months, "month")
            ?? FillWeekdays(parts[4], s._weekdays);
        if (bad is not null) { error = bad; return false; }
        schedule = s;
        error = null;
        return true;
    }

    /// <summary>The first run strictly after the given time (to the minute), or null if none exists in the next few years (for example 31 February).</summary>
    public DateTime? Next(DateTime afterUtc)
    {
        var t = new DateTime(afterUtc.Year, afterUtc.Month, afterUtc.Day, afterUtc.Hour, afterUtc.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
        var limit = t.AddYears(6);
        while (t < limit)
        {
            if (!_months[t.Month]) { t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1); continue; }
            if (!DayMatches(t)) { t = t.Date.AddDays(1); continue; }
            if (!_hours[t.Hour]) { t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc).AddHours(1); continue; }
            if (!_minutes[t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }
        return null;
    }

    /// <summary>The shortest gap between the next few runs; used to refuse schedules that would fire every few minutes.</summary>
    public TimeSpan? ShortestGap(DateTime fromUtc, int runs = 6)
    {
        TimeSpan? shortest = null;
        var previous = Next(fromUtc);
        for (var i = 1; i < runs && previous is not null; i++)
        {
            var next = Next(previous.Value);
            if (next is null) break;
            var gap = next.Value - previous.Value;
            if (shortest is null || gap < shortest) shortest = gap;
            previous = next;
        }
        return shortest;
    }

    private bool DayMatches(DateTime t)
    {
        var dayOk = _days[t.Day];
        var weekdayOk = _weekdays[(int)t.DayOfWeek];
        if (_dayStar && _weekdayStar) return true;
        if (_dayStar) return weekdayOk;
        if (_weekdayStar) return dayOk;
        return dayOk || weekdayOk;
    }

    private static string? FillWeekdays(string field, bool[] target)
    {
        var wide = new bool[8];
        var error = Fill(field, 0, 7, wide, "day of week");
        if (error is not null) return error;
        for (var i = 0; i < 7; i++) target[i] = wide[i] || (i == 0 && wide[7]);
        return null;
    }

    private static string? Fill(string field, int min, int max, bool[] target, string name)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1) return $"The step in the {name} field must be a whole number of 1 or more.";
                range = part[..slash];
            }

            int from, to;
            if (range == "*") { from = min; to = max; }
            else if (range.Contains('-'))
            {
                var ends = range.Split('-');
                if (ends.Length != 2 || !int.TryParse(ends[0], out from) || !int.TryParse(ends[1], out to)) return $"The {name} field has a range that isn't two numbers like 1-5.";
            }
            else
            {
                if (!int.TryParse(range, out from)) return $"The {name} field must use numbers, *, ranges like 1-5, lists like 1,15 or steps like */10.";
                to = slash >= 0 ? max : from; // "5/10" means from 5 to the end, every 10
            }

            if (from < min || to > max || from > to) return $"The {name} field must be between {min} and {max}.";
            for (var v = from; v <= to; v += step) target[v] = true;
        }
        return null;
    }
}

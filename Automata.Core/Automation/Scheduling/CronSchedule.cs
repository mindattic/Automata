using System.Globalization;

namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// A five-field cron expression — <c>minute hour day-of-month month day-of-week</c> — with
/// <c>*</c>, numbers, lists (<c>1,15</c>), ranges (<c>9-17</c>) and steps (<c>*/15</c>).
/// <para>
/// Hand-rolled rather than taking a dependency, matching Core's posture. The subset is the one
/// people actually write, and anything it does not understand is refused at parse time with a
/// reason rather than silently never firing — a schedule that quietly does nothing is the worst
/// possible failure mode for this feature.
/// </para>
/// </summary>
public sealed class CronSchedule
{
    /// <summary>How far ahead <see cref="Next"/> will look before giving up.</summary>
    public const int SearchDays = 366;

    private readonly bool[] minutes = new bool[60];
    private readonly bool[] hours = new bool[24];
    private readonly bool[] daysOfMonth = new bool[32];   // 1-31
    private readonly bool[] months = new bool[13];        // 1-12
    private readonly bool[] daysOfWeek = new bool[7];     // 0 = Sunday
    private readonly bool anyDayOfMonth;
    private readonly bool anyDayOfWeek;

    public string Expression { get; }

    private CronSchedule(string expression, string[] fields)
    {
        Expression = expression;
        Fill(minutes, fields[0], 0, 59);
        Fill(hours, fields[1], 0, 23);
        Fill(daysOfMonth, fields[2], 1, 31);
        Fill(months, fields[3], 1, 12);
        Fill(daysOfWeek, fields[4], 0, 6);
        anyDayOfMonth = fields[2].Trim() == "*";
        anyDayOfWeek = fields[4].Trim() == "*";
    }

    /// <summary>Parses an expression, or explains why it cannot.</summary>
    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "no cron expression";
            return false;
        }

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = $"expected 5 fields (minute hour day-of-month month day-of-week), got {fields.Length}";
            return false;
        }

        try
        {
            schedule = new CronSchedule(expression.Trim(), fields);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The next instant at or after <paramref name="after"/> that matches, in the given zone.
    /// Null when nothing matches within <see cref="SearchDays"/> — which for a valid expression
    /// means something like "31 February".
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        // Minute-by-minute, deliberately. A cleverer search would have to reason about month
        // lengths, weekday alignment and DST transitions all at once; stepping is obviously
        // correct, and a year of minutes is well under a millisecond of work per candidate.
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0)
            .AddMinutes(1);
        var limit = candidate.AddDays(SearchDays);

        while (candidate < limit)
        {
            if (Matches(candidate) && !zone.IsInvalidTime(candidate))
                return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
            candidate = candidate.AddMinutes(1);
        }
        return null;
    }

    private bool Matches(DateTime at)
    {
        if (!minutes[at.Minute] || !hours[at.Hour] || !months[at.Month]) return false;

        // Cron's oldest wart: when BOTH day fields are restricted, a match on EITHER counts. When
        // only one is restricted, only that one is consulted.
        var dayOfMonthOk = daysOfMonth[at.Day];
        var dayOfWeekOk = daysOfWeek[(int)at.DayOfWeek];
        if (anyDayOfMonth && anyDayOfWeek) return true;
        if (anyDayOfMonth) return dayOfWeekOk;
        if (anyDayOfWeek) return dayOfMonthOk;
        return dayOfMonthOk || dayOfWeekOk;
    }

    private static void Fill(bool[] target, string field, int min, int max)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var body = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                body = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) || step <= 0)
                    throw new FormatException($"'{part}' has an invalid step");
            }

            int from, to;
            if (body is "*" or "")
            {
                from = min;
                to = max;
            }
            else if (body.Contains('-'))
            {
                var ends = body.Split('-');
                if (ends.Length != 2 || !TryValue(ends[0], min, max, out from) || !TryValue(ends[1], min, max, out to))
                    throw new FormatException($"'{part}' is not a valid range");
            }
            else if (TryValue(body, min, max, out var single))
            {
                from = to = single;
            }
            else
            {
                throw new FormatException($"'{part}' is out of range {min}-{max}");
            }

            if (to < from) throw new FormatException($"'{part}' counts backwards");
            for (var value = from; value <= to; value += step) target[value] = true;
        }
    }

    private static bool TryValue(string text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;
}

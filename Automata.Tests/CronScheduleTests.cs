using Automata.Core.Automation.Scheduling;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class CronScheduleTests
{
    private static TimeZoneInfo Utc => TimeZoneInfo.Utc;

    /// <summary>Springs forward at 02:00 on 1 March, so the DST gap is deterministic instead of
    /// depending on the machine's timezone database.</summary>
    private static TimeZoneInfo GapZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Automata/CronGap", TimeSpan.Zero, "Gap", "Gap Standard", "Gap Daylight", [rule]);
    }

    private static CronSchedule Parse(string expression)
    {
        Assert.That(CronSchedule.TryParse(expression, out var schedule, out var error), Is.True, error);
        return schedule!;
    }

    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Test]
    public void EveryMinuteAdvancesByOneMinute()
    {
        var next = Parse("* * * * *").Next(At(2026, 5, 4, 10, 30), Utc);

        Assert.That(next, Is.EqualTo(At(2026, 5, 4, 10, 31)));
    }

    [Test]
    public void ADailyTimeFindsTodayWhenItIsStillAhead()
    {
        var next = Parse("0 9 * * *").Next(At(2026, 5, 4, 7, 0), Utc);

        Assert.That(next, Is.EqualTo(At(2026, 5, 4, 9, 0)));
    }

    [Test]
    public void ADailyTimeRollsToTomorrowOnceItHasPassed()
    {
        var next = Parse("0 9 * * *").Next(At(2026, 5, 4, 9, 0), Utc);

        Assert.That(next, Is.EqualTo(At(2026, 5, 5, 9, 0)),
            "the boundary is exclusive - a schedule must not re-fire for the instant it just ran");
    }

    [Test]
    public void StepsFireOnTheInterval()
    {
        var schedule = Parse("*/15 * * * *");

        Assert.That(schedule.Next(At(2026, 5, 4, 10, 0), Utc), Is.EqualTo(At(2026, 5, 4, 10, 15)));
        Assert.That(schedule.Next(At(2026, 5, 4, 10, 46), Utc), Is.EqualTo(At(2026, 5, 4, 11, 0)));
    }

    [Test]
    public void ListsAndRangesAreBothHonoured()
    {
        Assert.That(Parse("0 9,17 * * *").Next(At(2026, 5, 4, 10, 0), Utc), Is.EqualTo(At(2026, 5, 4, 17, 0)));
        Assert.That(Parse("30 9-11 * * *").Next(At(2026, 5, 4, 9, 45), Utc), Is.EqualTo(At(2026, 5, 4, 10, 30)));
    }

    [Test]
    public void ADayOfWeekPicksTheRightWeekday()
    {
        // 2026-05-04 is a Monday; the next Friday is the 8th.
        var next = Parse("0 8 * * 5").Next(At(2026, 5, 4, 0, 0), Utc);

        Assert.That(next, Is.EqualTo(At(2026, 5, 8, 8, 0)));
    }

    [Test]
    public void ADayOfMonthPicksTheRightDate()
    {
        var next = Parse("0 0 15 * *").Next(At(2026, 5, 4, 0, 0), Utc);

        Assert.That(next, Is.EqualTo(At(2026, 5, 15, 0, 0)));
    }

    /// <summary>
    /// Cron's oldest wart, and worth pinning: when BOTH day fields are restricted, a match on
    /// either one counts.
    /// </summary>
    [Test]
    public void WithBothDayFieldsRestricted_EitherMatching_Fires()
    {
        // The 6th (a Wednesday) matches day-of-month; the 8th is the next Friday.
        var schedule = Parse("0 0 6 * 5");

        Assert.That(schedule.Next(At(2026, 5, 4, 0, 0), Utc), Is.EqualTo(At(2026, 5, 6, 0, 0)));
        Assert.That(schedule.Next(At(2026, 5, 6, 0, 0), Utc), Is.EqualTo(At(2026, 5, 8, 0, 0)));
    }

    [Test]
    public void AMonthRestrictionIsHonoured()
    {
        var next = Parse("0 0 1 1 *").Next(At(2026, 5, 4, 0, 0), Utc);

        Assert.That(next, Is.EqualTo(At(2027, 1, 1, 0, 0)));
    }

    /// <summary>
    /// The time simply does not exist that day, so the schedule must skip it rather than fire at a
    /// moment the wall clock never showed.
    /// </summary>
    [Test]
    public void ATimeInsideASpringForwardGapIsSkipped()
    {
        var zone = GapZone();
        var next = Parse("30 2 * * *").Next(At(2026, 2, 28, 12, 0), zone);

        // 02:30 on 1 March does not exist, so the next real 02:30 is on the 2nd (01:30 UTC, since
        // daylight time is in force by then).
        Assert.That(next, Is.EqualTo(At(2026, 3, 2, 1, 30)));
    }

    [Test]
    public void ATimeOutsideTheGapStillFiresOnTheTransitionDay()
    {
        var zone = GapZone();
        var next = Parse("0 4 * * *").Next(At(2026, 3, 1, 0, 0), zone);

        Assert.That(next, Is.EqualTo(At(2026, 3, 1, 3, 0)), "04:00 daylight time is 03:00 UTC");
    }

    [Test]
    public void ADateThatNeverOccursReturnsNothingRatherThanSearchingForever()
    {
        // 31 February.
        var next = Parse("0 0 31 2 *").Next(At(2026, 5, 4, 0, 0), Utc);

        Assert.That(next, Is.Null);
    }

    // ---- parsing refuses rather than silently never firing ---------------------------------------

    [TestCase("", "no cron expression")]
    [TestCase("* * * *", "expected 5 fields")]
    [TestCase("* * * * * *", "expected 5 fields")]
    [TestCase("99 * * * *", "out of range")]
    [TestCase("* 25 * * *", "out of range")]
    [TestCase("17-9 * * * *", "backwards")]
    [TestCase("*/0 * * * *", "invalid step")]
    [TestCase("banana * * * *", "out of range")]
    public void AnExpressionItCannotHonourIsRefusedWithAReason(string expression, string expected)
    {
        var parsed = CronSchedule.TryParse(expression, out var schedule, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.False);
            Assert.That(schedule, Is.Null);
            Assert.That(error, Does.Contain(expected).IgnoreCase);
        });
    }

    [Test]
    public void TheExpressionIsKeptForDisplay()
    {
        Assert.That(Parse("  0 9 * * 1-5  ").Expression, Is.EqualTo("0 9 * * 1-5"));
    }
}

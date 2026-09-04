using System.IO;
using Automata.Core.Automation.Scheduling;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class TriggerEvaluatorTests
{
    private static DateTimeOffset At(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static ScheduleEntry Entry(params TriggerDefinition[] triggers) => new()
    {
        Id = "e1", Name = "Nightly", TargetId = "c1", Triggers = [.. triggers],
    };

    private static TriggerDefinition Cron(string expression, CatchUpPolicy catchUp = CatchUpPolicy.Skip) => new()
    {
        Kind = TriggerKind.Cron, CronExpression = expression, TimeZoneId = "UTC", CatchUp = catchUp,
    };

    private static TriggerDefinition Interval(int seconds, DateTimeOffset? anchor = null) => new()
    {
        Kind = TriggerKind.Interval, IntervalSeconds = seconds, AnchorUtc = anchor,
    };

    // ---- first scheduling ------------------------------------------------------------------------

    /// <summary>
    /// A brand-new schedule must not fire the moment it is created — "every day at 09:00" means the
    /// next 09:00, not right now.
    /// </summary>
    [Test]
    public void ANewEntryIsNotDueImmediately_ItGetsAFirstDueTime()
    {
        var clock = new FakeClock(At(2026, 5, 4, 7, 0));

        var verdict = TriggerEvaluator.Evaluate(Entry(Cron("0 9 * * *")), clock);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Due, Is.False);
            Assert.That(verdict.NextUtc, Is.EqualTo(At(2026, 5, 4, 9, 0)));
            Assert.That(verdict.Reason, Does.Contain("first run"));
        });
    }

    [Test]
    public void AnEntryWithOnlyManualTriggersSaysSo()
    {
        var verdict = TriggerEvaluator.Evaluate(Entry(), new FakeClock(At(2026, 5, 4, 7, 0)));

        Assert.That(verdict.Due, Is.False);
        Assert.That(verdict.Reason, Does.Contain("by hand"));
    }

    [Test]
    public void ADisabledEntryIsNeverDue()
    {
        var entry = Entry(Cron("* * * * *"));
        entry.Enabled = false;
        entry.NextDueUtc = At(2026, 5, 4, 6, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 7, 0)));

        Assert.That(verdict.Due, Is.False);
        Assert.That(verdict.Reason, Is.EqualTo("disabled"));
    }

    /// <summary>An expression that can never match must say so rather than sitting silently at
    /// "not due" forever.</summary>
    [Test]
    public void ATriggerThatCanNeverFireIsCalledOut()
    {
        var verdict = TriggerEvaluator.Evaluate(Entry(Cron("0 0 31 2 *")), new FakeClock(At(2026, 5, 4, 7, 0)));

        Assert.That(verdict.NextUtc, Is.Null);
        Assert.That(verdict.Reason, Does.Contain("will ever fire"));
    }

    // ---- becoming due ----------------------------------------------------------------------------

    [Test]
    public void AnEntryBecomesDueOnceItsWrittenDownTimeArrives()
    {
        var entry = Entry(Cron("0 9 * * *"));
        entry.NextDueUtc = At(2026, 5, 4, 9, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 9, 0)));

        Assert.That(verdict.Due, Is.True);
        Assert.That(verdict.NextUtc, Is.EqualTo(At(2026, 5, 5, 9, 0)), "and the next one is already worked out");
    }

    [Test]
    public void BeforeItsTimeItReportsHowLongIsLeft()
    {
        var entry = Entry(Cron("0 9 * * *"));
        entry.NextDueUtc = At(2026, 5, 4, 9, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 8, 30)));

        Assert.That(verdict.Due, Is.False);
        Assert.That(verdict.Reason, Does.Contain("30m"));
    }

    /// <summary>
    /// The due time is written down rather than recomputed from "now", which is exactly what lets a
    /// firing survive the process not running between ticks.
    /// </summary>
    [Test]
    public void AFiringMissedByASmallGapStillRuns()
    {
        var entry = Entry(Cron("0 9 * * *"));
        entry.NextDueUtc = At(2026, 5, 4, 9, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 9, 3)));

        Assert.That(verdict.Due, Is.True, "a tick three minutes late must still honour the firing");
    }

    /// <summary>
    /// The default for a long outage. A batch of missed runs all firing at once after a machine was
    /// off is rarely what anyone meant by "every hour".
    /// </summary>
    [Test]
    public void AFiringMissedByALongOutageIsSkippedByDefault()
    {
        var entry = Entry(Cron("0 9 * * *"));
        entry.NextDueUtc = At(2026, 5, 1, 9, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 10, 0)));

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Due, Is.False);
            Assert.That(verdict.Reason, Does.Contain("skipped"));
            Assert.That(verdict.NextUtc, Is.EqualTo(At(2026, 5, 5, 9, 0)));
        });
    }

    [Test]
    public void CatchUpRunOnceImmediately_StillRunsAfterALongOutage()
    {
        var entry = Entry(Cron("0 9 * * *", CatchUpPolicy.RunOnceImmediately));
        entry.NextDueUtc = At(2026, 5, 1, 9, 0);

        var verdict = TriggerEvaluator.Evaluate(entry, new FakeClock(At(2026, 5, 4, 10, 0)));

        Assert.That(verdict.Due, Is.True);
    }

    // ---- intervals -------------------------------------------------------------------------------

    /// <summary>Anchored rather than "now + interval", so an hourly job stays on the hour instead of
    /// drifting later after every restart.</summary>
    [Test]
    public void AnIntervalLandsOnItsAnchorGrid()
    {
        var anchor = At(2026, 5, 4, 0, 0);
        var trigger = Interval(3600, anchor);

        Assert.That(TriggerEvaluator.Next(trigger, At(2026, 5, 4, 10, 17)), Is.EqualTo(At(2026, 5, 4, 11, 0)));
        Assert.That(TriggerEvaluator.Next(trigger, At(2026, 5, 4, 10, 0)), Is.EqualTo(At(2026, 5, 4, 11, 0)));
    }

    [Test]
    public void AnIntervalAnchoredInTheFutureWaitsForItsAnchor()
    {
        var anchor = At(2026, 5, 4, 12, 0);

        Assert.That(TriggerEvaluator.Next(Interval(3600, anchor), At(2026, 5, 4, 10, 0)), Is.EqualTo(anchor));
    }

    [Test]
    public void ANonPositiveIntervalNeverFires()
    {
        Assert.That(TriggerEvaluator.Next(Interval(0), At(2026, 5, 4, 10, 0)), Is.Null);
    }

    [Test]
    public void AOneShotFiresOnceAndThenNeverAgain()
    {
        var trigger = new TriggerDefinition { Kind = TriggerKind.OneShot, FireAtUtc = At(2026, 5, 4, 9, 0) };

        Assert.That(TriggerEvaluator.Next(trigger, At(2026, 5, 4, 8, 0)), Is.EqualTo(At(2026, 5, 4, 9, 0)));
        Assert.That(TriggerEvaluator.Next(trigger, At(2026, 5, 4, 9, 0)), Is.Null);
    }

    [Test]
    public void TheSoonestOfSeveralTriggersWins()
    {
        var next = TriggerEvaluator.NextAcross(
            [Cron("0 9 * * *"), Interval(600, At(2026, 5, 4, 0, 0))], At(2026, 5, 4, 8, 0));

        Assert.That(next, Is.EqualTo(At(2026, 5, 4, 8, 10)));
    }

    /// <summary>
    /// The mixture the multi-trigger editor makes reachable: "every weekday at 09:00 AND once the
    /// ingest has finished". The two answer different questions and must not interfere — the clock
    /// decides when this entry is due on its own, and the chain starts it regardless of that.
    /// </summary>
    [Test]
    public void AClockTriggerAndAnAfterEntryTriggerBothApply()
    {
        var entry = Entry(
            Cron("0 9 * * *"),
            new TriggerDefinition { Kind = TriggerKind.AfterEntry, AfterEntryId = "upstream" });
        var clock = new FakeClock(At(2026, 5, 4, 7, 0));

        var verdict = TriggerEvaluator.Evaluate(entry, clock);
        Assert.That(verdict.NextUtc, Is.EqualTo(At(2026, 5, 4, 9, 0)),
            "the clock trigger still gives it a due time of its own");
        Assert.That(verdict.Reason, Does.Not.Contain("waits for"),
            "an entry with a clock does not read as merely waiting on something else");

        Assert.That(TriggerEvaluator.Dependents([entry], "upstream", succeeded: true).Single(), Is.SameAs(entry),
            "and the upstream finishing still starts it, independently of the clock");
    }

    /// <summary>
    /// The soonest firing has to be found across triggers of DIFFERENT kinds, not just several of
    /// one — which is exactly what an entry built as "every day at 09:00, or every 30 minutes" is.
    /// </summary>
    [Test]
    public void TheSoonestWinsAcrossMixedTriggerKinds()
    {
        var entry = Entry(
            Cron("0 9 * * *"),
            new TriggerDefinition { Kind = TriggerKind.OneShot, FireAtUtc = At(2026, 5, 4, 7, 30) });
        var clock = new FakeClock(At(2026, 5, 4, 7, 0));

        Assert.That(TriggerEvaluator.Evaluate(entry, clock).NextUtc, Is.EqualTo(At(2026, 5, 4, 7, 30)));
    }

    [Test]
    public void ADisabledTriggerIsIgnored()
    {
        var disabled = Cron("* * * * *");
        disabled.Enabled = false;

        Assert.That(TriggerEvaluator.Next(disabled, At(2026, 5, 4, 8, 0)), Is.Null);
    }

    // ---- chaining --------------------------------------------------------------------------------

    private static ScheduleEntry After(string id, string upstream, UpstreamOutcome outcome = UpstreamOutcome.Succeeded) => new()
    {
        Id = id, Name = id, TargetId = "c-" + id,
        Triggers = [new TriggerDefinition
        {
            Kind = TriggerKind.AfterEntry, AfterEntryId = upstream, RequiredOutcome = outcome,
        }],
    };

    [Test]
    public void SuccessStartsWhateverWasWaitingOnIt()
    {
        var entries = new List<ScheduleEntry> { Entry(Cron("0 9 * * *")), After("e2", "e1") };

        var started = TriggerEvaluator.Dependents(entries, "e1", succeeded: true).Select(e => e.Id);

        Assert.That(started, Is.EqualTo(new[] { "e2" }));
    }

    [Test]
    public void AFailureDoesNotStartSomethingThatWantedSuccess()
    {
        var entries = new List<ScheduleEntry> { Entry(Cron("0 9 * * *")), After("e2", "e1") };

        Assert.That(TriggerEvaluator.Dependents(entries, "e1", succeeded: false), Is.Empty);
    }

    [Test]
    public void AnOnFailureEntryStartsOnlyOnFailure()
    {
        var entries = new List<ScheduleEntry>
        {
            Entry(Cron("0 9 * * *")),
            After("alert", "e1", UpstreamOutcome.Failed),
        };

        Assert.That(TriggerEvaluator.Dependents(entries, "e1", false).Select(e => e.Id), Is.EqualTo(new[] { "alert" }));
        Assert.That(TriggerEvaluator.Dependents(entries, "e1", true), Is.Empty);
    }

    [Test]
    public void AnOnCompletedEntryStartsEitherWay()
    {
        var entries = new List<ScheduleEntry>
        {
            Entry(Cron("0 9 * * *")),
            After("always", "e1", UpstreamOutcome.Completed),
        };

        Assert.That(TriggerEvaluator.Dependents(entries, "e1", true), Is.Not.Empty);
        Assert.That(TriggerEvaluator.Dependents(entries, "e1", false), Is.Not.Empty);
    }

    [Test]
    public void AChainIsFollowedInOrder()
    {
        var entries = new List<ScheduleEntry>
        {
            Entry(Cron("0 9 * * *")), After("e2", "e1"), After("e3", "e2"), After("e4", "e3"),
        };

        var chain = TriggerEvaluator.Chain(entries, "e1", succeeded: true).Select(e => e.Id);

        Assert.That(chain, Is.EqualTo(new[] { "e2", "e3", "e4" }));
    }

    /// <summary>
    /// Chains are the point of after-triggers, so they are followed rather than forbidden — but an
    /// entry appears once per chain, so a loop exhausts itself instead of running forever.
    /// </summary>
    [Test]
    public void ACycleExhaustsItselfInsteadOfLoopingForever()
    {
        var entries = new List<ScheduleEntry> { After("e1", "e2"), After("e2", "e1") };

        var chain = TriggerEvaluator.Chain(entries, "e1", succeeded: true).Select(e => e.Id).ToList();

        Assert.That(chain, Is.EqualTo(new[] { "e2" }), "e1 must not be scheduled again by its own dependent");
    }

    [Test]
    public void ADisabledDependentIsNotStarted()
    {
        var dependent = After("e2", "e1");
        dependent.Enabled = false;

        Assert.That(TriggerEvaluator.Dependents([Entry(Cron("* * * * *")), dependent], "e1", true), Is.Empty);
    }
}

[TestFixture]
public class ScheduleStoreTests
{
    private string path = null!;

    [SetUp]
    public void SetUp() => path = Path.Combine(
        Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"), "schedule.json");

    [TearDown]
    public void TearDown()
    {
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Test]
    public void AFreshMachineHasAnEmptyScheduleAndNoFile()
    {
        Assert.That(new ScheduleStore(path).Load(), Is.Empty);
        Assert.That(File.Exists(path), Is.False, "nothing is written until something is scheduled");
    }

    [Test]
    public void AnEntryRoundTrips()
    {
        var store = new ScheduleStore(path);
        store.Upsert(new ScheduleEntry
        {
            Id = "e1", Name = "Nightly", Target = ScheduleTargetKind.Collection, TargetId = "c1",
            Triggers =
            [
                new TriggerDefinition
                {
                    Kind = TriggerKind.Cron, CronExpression = "0 9 * * *", TimeZoneId = "UTC",
                    CatchUp = CatchUpPolicy.RunOnceImmediately,
                },
            ],
            NextDueUtc = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero),
        });

        var back = new ScheduleStore(path).Get("e1")!;

        Assert.Multiple(() =>
        {
            Assert.That(back.Name, Is.EqualTo("Nightly"));
            Assert.That(back.Triggers.Single().CronExpression, Is.EqualTo("0 9 * * *"));
            Assert.That(back.Triggers.Single().CatchUp, Is.EqualTo(CatchUpPolicy.RunOnceImmediately));
            Assert.That(back.NextDueUtc, Is.EqualTo(new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero)));
        });
    }

    [Test]
    public void UpsertReplacesRatherThanDuplicating()
    {
        var store = new ScheduleStore(path);
        store.Upsert(new ScheduleEntry { Id = "e1", Name = "First" });
        store.Upsert(new ScheduleEntry { Id = "e1", Name = "Renamed" });

        Assert.That(store.Load(), Has.Count.EqualTo(1));
        Assert.That(store.Get("e1")!.Name, Is.EqualTo("Renamed"));
    }

    [Test]
    public void RemoveReportsWhetherAnythingWasThere()
    {
        var store = new ScheduleStore(path);
        store.Upsert(new ScheduleEntry { Id = "e1" });

        Assert.That(store.Remove("e1"), Is.True);
        Assert.That(store.Remove("e1"), Is.False);
        Assert.That(store.Load(), Is.Empty);
    }

    /// <summary>A corrupt schedule must not stop the app starting; nothing fires until it is fixed.</summary>
    [Test]
    public void ACorruptFileLoadsAsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        Assert.That(new ScheduleStore(path).Load(), Is.Empty);
    }
}

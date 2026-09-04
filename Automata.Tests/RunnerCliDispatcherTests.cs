using System.IO;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Automata.Tests.Fakes;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class RunnerCliDispatcherTests
{
    private string root = null!;
    private CollectionStore collections = null!;
    private DatasetStore datasets = null!;
    private RunStore runs = null!;
    private AutomataSettingsStore settings = null!;
    private FakeBrowserSurfaceFactory factory = null!;
    private StringWriter output = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
        datasets = new DatasetStore(Path.Combine(root, "datasets"));
        runs = new RunStore(Path.Combine(root, "runs"));
        settings = new AutomataSettingsStore(Path.Combine(root, "settings.json"));
        factory = new FakeBrowserSurfaceFactory();
        output = new StringWriter();
        schedule = new ScheduleStore(Path.Combine(root, "schedule.json"));
        clock = new FakeClock(new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero));
        registrar = new FakeRegistrar();
        parked = new ParkedRunStore(Path.Combine(root, "parked"));
        live = new LiveLaneStore(Path.Combine(root, "live"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private ScheduleStore schedule = null!;
    private ParkedRunStore parked = null!;
    private LiveLaneStore live = null!;
    private FakeClock clock = null!;
    private FakeRegistrar registrar = null!;

    private RunnerCliDispatcher Dispatcher() => new(
        collections,
        runs,
        new WorkflowEngine(new ReplayEngine(new FingerprintResolver { PollIntervalMs = 10 }), collections, datasets),
        settings,
        factory,
        output,
        schedule,
        clock,
        registrar,
        parked,
        live);

    /// <summary>Records what would have been registered, so the CLI is testable without touching
    /// the machine's real Task Scheduler.</summary>
    private sealed class FakeRegistrar : IScheduledTaskRegistrar
    {
        public int? InstalledEvery { get; private set; }
        public bool Uninstalled { get; private set; }

        public Task<string> InstallAsync(int intervalMinutes, CancellationToken ct)
        {
            InstalledEvery = intervalMinutes;
            return Task.FromResult($"registered every {intervalMinutes} minute(s)");
        }

        public Task<string> UninstallAsync(CancellationToken ct)
        {
            Uninstalled = true;
            return Task.FromResult("removed");
        }
    }

    private TaskDefinition SeedTask(string collectionName, string taskName, params Step[] steps)
    {
        var collection = collections.LoadCollections().FirstOrDefault(c => c.Name == collectionName)
            ?? collections.CreateCollection(collectionName);
        var task = new TaskDefinition { CollectionId = collection.Id, Name = taskName, Steps = [.. steps] };
        collections.SaveTask(task);
        return task;
    }

    private static Step Nav(string url = "https://x.example") =>
        new() { Id = Guid.NewGuid().ToString("n"), Action = StepAction.Navigate, Label = "Go", Url = url };

    private string Written => output.ToString();

    // ---- argument handling ---------------------------------------------------------------------

    [Test]
    public async Task NoArgumentsPrintsUsageAndReportsBadArguments()
    {
        var code = await Dispatcher().DispatchAsync([]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("automata-runner"));
    }

    [TestCase("--help")]
    [TestCase("-h")]
    [TestCase("help")]
    public async Task AskingForHelpSucceeds(string arg)
    {
        Assert.That(await Dispatcher().DispatchAsync([arg]), Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("Exit codes"));
    }

    /// <summary>The session-0 constraint is a real limit on unattended running, so the help says so
    /// rather than leaving someone to discover it from a task that silently never works.</summary>
    [Test]
    public async Task HelpSaysLanesNeedAnInteractiveSession()
    {
        await Dispatcher().DispatchAsync(["--help"]);

        Assert.That(Written, Does.Contain("logged on"));
    }

    [Test]
    public async Task AnUnknownCommandIsRejected()
    {
        var code = await Dispatcher().DispatchAsync(["frobnicate"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("unknown command"));
    }

    [Test]
    public async Task RunNeedsATarget()
    {
        var code = await Dispatcher().DispatchAsync(["run"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("--task"));
    }

    [Test]
    public async Task RunRefusesBothTargetsAtOnce()
    {
        var code = await Dispatcher().DispatchAsync(["run", "--task", "a", "--collection", "b"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("not both"));
    }

    [Test]
    public async Task AMissingTargetIsReportedByName()
    {
        var code = await Dispatcher().DispatchAsync(["run", "--task", "Ghost"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("Ghost"));
    }

    // ---- running -------------------------------------------------------------------------------

    [Test]
    public async Task RunsATaskByNameAndReportsSuccess()
    {
        SeedTask("C", "Open it", Nav());

        var code = await Dispatcher().DispatchAsync(["run", "--task", "Open it"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(Written, Does.Contain("1/1 task(s) passed"));
            Assert.That(factory.Requested, Has.Count.EqualTo(1), "the run should have opened one browser lane");
        });
    }

    [Test]
    public async Task RunsATaskById()
    {
        var task = SeedTask("C", "By id", Nav());

        Assert.That(await Dispatcher().DispatchAsync(["run", "--task", task.Id]), Is.EqualTo(RunnerExitCode.Success));
    }

    [Test]
    public async Task RunsEveryTaskInACollection()
    {
        SeedTask("Batch", "One", Nav("https://one.example"));
        SeedTask("Batch", "Two", Nav("https://two.example"));

        var code = await Dispatcher().DispatchAsync(["run", "--collection", "Batch"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("2/2 task(s) passed"));
    }

    [Test]
    public async Task AnEmptyCollectionIsNotAFailure()
    {
        collections.CreateCollection("Empty");

        var code = await Dispatcher().DispatchAsync(["run", "--collection", "Empty"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("no tasks"));
    }

    /// <summary>Exit code 1 is what a scheduled task or a CI step branches on.</summary>
    [Test]
    public async Task AFailedRunExitsOne()
    {
        SeedTask("C", "Broken", new Step
        {
            Id = "s1", Action = StepAction.Click, Label = "Click",
            Target = new ElementFingerprint { CssSelector = "#nope" },
            TimeoutMs = 5,
        });
        factory.Responder = script => script.Contains("__automataResolve(")
            ? """{ "found": false, "ambiguous": false, "candidateCount": 0 }"""
            : """{ "isProcessing": false }""";

        var code = await Dispatcher().DispatchAsync(["run", "--task", "Broken"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.RunFailed));
        Assert.That(Written, Does.Contain("FAILED"));
    }

    [Test]
    public async Task ARunIsRecordedInTheRunStore()
    {
        SeedTask("C", "Recorded", Nav());

        await Dispatcher().DispatchAsync(["run", "--task", "Recorded"]);

        var run = runs.ListRuns().Single();
        Assert.Multiple(() =>
        {
            Assert.That(run.TargetName, Is.EqualTo("Recorded"));
            Assert.That(run.Success, Is.True);
            Assert.That(run.Summary, Does.Contain("1/1"));
            Assert.That(run.EndedUtc, Is.Not.Null);
        });
    }

    /// <summary>The value ExtractText captures reaches the run store, which is where a later run
    /// or the sidebar can read it back.</summary>
    [Test]
    public async Task ExtractedValuesArePersistedWithTheRun()
    {
        var task = SeedTask("C", "Reads", new Step
        {
            Id = "read", Action = StepAction.ExtractText,
            Target = new ElementFingerprint { CssSelector = "#total" },
            Outputs = [new OutputField { Name = "total" }],
        });
        factory.Responder = script =>
        {
            if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
            if (script.Contains("__automataResolve(")) return """
                { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
                  "candidateCount": 1, "centerX": 1, "centerY": 2 }
                """;
            if (script.Contains("textContent")) return """{ "ok": true, "value": "$42.00" }""";
            return "{}";
        };

        await Dispatcher().DispatchAsync(["run", "--task", "Reads"]);

        var run = runs.ListRuns().Single();
        Assert.That(runs.LoadOutputs(run.RunId, task.Id)["read"]["text"], Is.EqualTo("$42.00"));
    }

    // ---- status --------------------------------------------------------------------------------

    [Test]
    public async Task StatusOnAFreshMachineSaysSoRatherThanFailing()
    {
        var code = await Dispatcher().DispatchAsync(["status"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("No runs recorded yet"));
    }

    [Test]
    public async Task StatusListsRecentRunsNewestFirst()
    {
        SeedTask("C", "First", Nav());
        await Dispatcher().DispatchAsync(["run", "--task", "First"]);
        output.GetStringBuilder().Clear();

        var code = await Dispatcher().DispatchAsync(["status"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("First").And.Contain("passed"));
    }

    // ---- schedule --------------------------------------------------------------------------------

    [Test]
    public async Task ScheduleListOnAFreshMachineSaysSo()
    {
        Assert.That(await Dispatcher().DispatchAsync(["schedule", "list"]), Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("Nothing scheduled"));
    }

    [Test]
    public async Task ScheduleAddRegistersACronEntryAndReportsWhenItFires()
    {
        SeedTask("Nightly", "Job", Nav());

        var code = await Dispatcher().DispatchAsync(
            ["schedule", "add", "--collection", "Nightly", "--cron", "0 9 * * *", "--timezone", "UTC"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(schedule.Load(), Has.Count.EqualTo(1));
            Assert.That(schedule.Load()[0].NextDueUtc,
                Is.EqualTo(new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero)));
            // Reported after the entry is saved, so its due time is already written down: "next in 1h".
            Assert.That(Written, Does.Contain("in 1h"));
        });
    }

    /// <summary>
    /// Refused at the point of scheduling rather than accepted and silently never firing, which is
    /// the worst failure mode a schedule can have.
    /// </summary>
    [Test]
    public async Task ABadCronExpressionIsRefusedWhenItIsScheduled()
    {
        SeedTask("Nightly", "Job", Nav());

        var code = await Dispatcher().DispatchAsync(
            ["schedule", "add", "--collection", "Nightly", "--cron", "99 * * * *"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.BadArguments));
        Assert.That(Written, Does.Contain("out of range"));
        Assert.That(schedule.Load(), Is.Empty);
    }

    [Test]
    public async Task ScheduleAddNeedsATargetAndATrigger()
    {
        Assert.That(await Dispatcher().DispatchAsync(["schedule", "add", "--cron", "* * * * *"]),
            Is.EqualTo(RunnerExitCode.BadArguments));
        SeedTask("C", "Job", Nav());
        Assert.That(await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C"]),
            Is.EqualTo(RunnerExitCode.BadArguments));
    }

    [Test]
    public async Task ScheduleEnableDisableAndRemoveWorkOnAnIdPrefix()
    {
        SeedTask("C", "Job", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C", "--every-minutes", "30"]);
        var id = schedule.Load()[0].Id[..8];

        await Dispatcher().DispatchAsync(["schedule", "disable", id]);
        Assert.That(schedule.Load()[0].Enabled, Is.False);

        await Dispatcher().DispatchAsync(["schedule", "enable", id]);
        Assert.That(schedule.Load()[0].Enabled, Is.True);

        await Dispatcher().DispatchAsync(["schedule", "remove", id]);
        Assert.That(schedule.Load(), Is.Empty);
    }

    // ---- tick ------------------------------------------------------------------------------------

    [Test]
    public async Task TickWithNothingScheduledSaysSo()
    {
        Assert.That(await Dispatcher().DispatchAsync(["tick"]), Is.EqualTo(RunnerExitCode.Success));
        Assert.That(Written, Does.Contain("Nothing scheduled"));
    }

    [Test]
    public async Task TickBeforeAnythingIsDueRunsNothing()
    {
        SeedTask("C", "Job", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C", "--cron", "0 9 * * *", "--timezone", "UTC"]);
        output.GetStringBuilder().Clear();

        await Dispatcher().DispatchAsync(["tick"]);

        Assert.That(Written, Does.Contain("Nothing due"));
        Assert.That(runs.ListRuns(), Is.Empty);
    }

    [Test]
    public async Task TickRunsWhatIsDueAndRecordsTheOutcome()
    {
        SeedTask("C", "Job", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C", "--cron", "0 9 * * *", "--timezone", "UTC"]);
        clock.Advance(TimeSpan.FromHours(1));   // 09:00
        output.GetStringBuilder().Clear();

        var code = await Dispatcher().DispatchAsync(["tick"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(Written, Does.Contain("Ran 1 scheduled item"));
            Assert.That(runs.ListRuns(), Has.Count.EqualTo(1));
            Assert.That(schedule.Load()[0].LastOutcome, Is.EqualTo("passed"));
            Assert.That(schedule.Load()[0].NextDueUtc,
                Is.EqualTo(new DateTimeOffset(2026, 5, 5, 9, 0, 0, TimeSpan.Zero)),
                "the next firing is written down before the tick ends");
        });
    }

    [Test]
    public async Task TickDoesNotRunTheSameFiringTwice()
    {
        SeedTask("C", "Job", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C", "--cron", "0 9 * * *", "--timezone", "UTC"]);
        clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().DispatchAsync(["tick"]);
        await Dispatcher().DispatchAsync(["tick"]);

        Assert.That(runs.ListRuns(), Has.Count.EqualTo(1));
    }

    /// <summary>Chains are the point of after-triggers: "once the ingest finishes, reconcile".</summary>
    [Test]
    public async Task TickFollowsWhatAFinishedEntryStarts()
    {
        SeedTask("Ingest", "Pull", Nav());
        SeedTask("Reconcile", "Match", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "Ingest", "--cron", "0 9 * * *", "--timezone", "UTC"]);
        var ingestId = schedule.Load()[0].Id;
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "Reconcile", "--after", ingestId]);
        clock.Advance(TimeSpan.FromHours(1));
        output.GetStringBuilder().Clear();

        await Dispatcher().DispatchAsync(["tick"]);

        Assert.Multiple(() =>
        {
            Assert.That(Written, Does.Contain("starts: Reconcile"));
            Assert.That(runs.ListRuns().Select(r => r.TargetName),
                Is.EquivalentTo(new[] { "Ingest", "Reconcile" }));
        });
    }

    [Test]
    public async Task ADisabledEntryIsNotRunByATick()
    {
        SeedTask("C", "Job", Nav());
        await Dispatcher().DispatchAsync(["schedule", "add", "--collection", "C", "--cron", "0 9 * * *", "--timezone", "UTC"]);
        await Dispatcher().DispatchAsync(["schedule", "disable", schedule.Load()[0].Id[..8]]);
        clock.Advance(TimeSpan.FromHours(1));

        await Dispatcher().DispatchAsync(["tick"]);

        Assert.That(runs.ListRuns(), Is.Empty);
    }

    // ---- install ---------------------------------------------------------------------------------

    [Test]
    public async Task InstallRegistersTheHeartbeatAndExplainsTheLoggedOnRequirement()
    {
        var code = await Dispatcher().DispatchAsync(["install", "--interval-minutes", "10"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(registrar.InstalledEvery, Is.EqualTo(10));
            Assert.That(Written, Does.Contain("session 0"),
                "the session-0 limit is why this is a logged-on task, and the user should be told");
        });
    }

    [Test]
    public async Task InstallDefaultsToFiveMinutes()
    {
        await Dispatcher().DispatchAsync(["install"]);

        Assert.That(registrar.InstalledEvery, Is.EqualTo(5));
    }

    [Test]
    public async Task UninstallRemovesIt()
    {
        Assert.That(await Dispatcher().DispatchAsync(["uninstall"]), Is.EqualTo(RunnerExitCode.Success));
        Assert.That(registrar.Uninstalled, Is.True);
    }

    // ---- parked runs ---------------------------------------------------------------------------

    /// <summary>A wait far enough ahead that the default 15-minute park threshold is exceeded.</summary>
    private static Step LongWait(string id) => new()
    {
        Id = id, Action = StepAction.Wait, Label = "Wait until 09:00",
        Wait = new WaitSpec { Mode = WaitMode.UntilTimeOfDay, TimeOfDay = new TimeOnly(9, 0), TimeZoneId = "UTC" },
    };

    [Test]
    public async Task ARunThatParksLeavesACheckpointAndAnOpenRunRecord()
    {
        var task = SeedTask("Nightly", "Batch", Nav(), LongWait("w"), Nav());

        var code = await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);

        Assert.That(code, Is.EqualTo(RunnerExitCode.Success), "parking is not a failure");
        var entry = parked.List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.TaskId, Is.EqualTo(task.Id));
            Assert.That(entry.Checkpoint.ResumeStepId, Is.EqualTo("w"));
            Assert.That(entry.Checkpoint.ResumePath, Is.EqualTo(new[] { 1 }));
            Assert.That(entry.TotalTasks, Is.EqualTo(1));
            Assert.That(Written, Does.Contain("Parked"));
            // The run has NOT finished, so its manifest must stay open — a completed manifest
            // would claim an outcome and drop the run out of the "still owed" list.
            Assert.That(runs.ListRuns().Single().Success, Is.Null);
        });
    }

    [Test]
    public async Task TickResumesAParkedRunOnceItsWaitIsOver()
    {
        SeedTask("Nightly", "Batch", Nav(), LongWait("w"), Nav("https://after.example"));
        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);
        output.GetStringBuilder().Clear();

        // Before the wait ends, nothing happens.
        Assert.That(await Dispatcher().DispatchAsync(["tick"]), Is.EqualTo(RunnerExitCode.Success));
        Assert.That(parked.List(), Is.Not.Empty, "it is not due yet");

        clock.Advance(TimeSpan.FromHours(2));   // 08:00 -> 10:00, past the 09:00 wait
        output.GetStringBuilder().Clear();

        var code = await Dispatcher().DispatchAsync(["tick"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.Success));
            Assert.That(parked.List(), Is.Empty, "a resumed run leaves no checkpoint behind");
            Assert.That(Written, Does.Contain("Resuming"));
            var run = runs.ListRuns().Single();
            Assert.That(run.Success, Is.True, "the run should have finished this time");
            Assert.That(run.Summary, Does.Contain("1/1 task(s) passed"));
        });
        // The step after the wait actually ran.
        Assert.That(factory.Lanes.SelectMany(l => l.Fake.Calls).Any(c => c.Args == "https://after.example"),
            Is.True, "resuming must carry on from the step after the wait");
    }

    /// <summary>
    /// The reason parked runs are handled before schedules and without requiring one: a manually
    /// started run can park just as easily as a scheduled one, and it still has to be finished.
    /// </summary>
    [Test]
    public async Task TickResumesAParkedRunEvenWithNothingScheduled()
    {
        SeedTask("Nightly", "Batch", LongWait("w"), Nav("https://after.example"));
        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);
        clock.Advance(TimeSpan.FromHours(2));
        output.GetStringBuilder().Clear();

        await Dispatcher().DispatchAsync(["tick"]);

        Assert.That(Written, Does.Contain("Resumed 1 parked run"));
        Assert.That(Written, Does.Contain("nothing scheduled"));
        Assert.That(parked.List(), Is.Empty);
    }

    [Test]
    public async Task AParkedCollectionCarriesOnThroughTheRestOfItsTasks()
    {
        SeedTask("Nightly", "First", LongWait("w"), Nav("https://after-first.example"));
        SeedTask("Nightly", "Second", Nav("https://second.example"));

        await Dispatcher().DispatchAsync(["run", "--collection", "Nightly"]);

        var entry = parked.List().Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.TotalTasks, Is.EqualTo(2));
            Assert.That(entry.RemainingTaskIds, Has.Count.EqualTo(1),
                "the task queued behind the parked one has to be remembered, or it would never run");
            Assert.That(entry.TasksPassed, Is.Zero);
        });

        clock.Advance(TimeSpan.FromHours(2));
        await Dispatcher().DispatchAsync(["tick"]);

        var run = runs.ListRuns().Single();
        Assert.That(run.Summary, Does.Contain("2/2 task(s) passed"),
            "the summary has to count the whole collection, not only what ran after the wait");
        Assert.That(factory.Lanes.SelectMany(l => l.Fake.Calls).Any(c => c.Args == "https://second.example"),
            Is.True, "the queued task should have run after the resumed one");
    }

    [Test]
    public async Task AParkedRunWhoseTaskWasDeletedFailsRatherThanRetryingForever()
    {
        var task = SeedTask("Nightly", "Batch", LongWait("w"), Nav());
        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);
        collections.DeleteTask(task.Id);
        clock.Advance(TimeSpan.FromHours(2));
        output.GetStringBuilder().Clear();

        var code = await Dispatcher().DispatchAsync(["tick"]);

        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(RunnerExitCode.RunFailed));
            Assert.That(parked.List(), Is.Empty, "a checkpoint that can never resume must not be retried every tick");
            Assert.That(runs.ListRuns().Single().Success, Is.False);
            Assert.That(Written, Does.Contain("no longer exists"));
        });
    }

    // ---- the live lane strip -------------------------------------------------------------------

    /// <summary>
    /// The runner is headless, so the lanes it opens are invisible unless it publishes them. This
    /// checks from INSIDE a running step — the browser's own eval is the only hook that fires while
    /// a lane is genuinely busy — that the lane is on record at that moment, and gone afterwards.
    /// </summary>
    [Test]
    public async Task ARunPublishesItsLanesWhileTheyAreBusyAndClearsThemAfterwards()
    {
        SeedTask("Nightly", "Batch", Nav());
        IReadOnlyList<(LiveLanes Process, LaneStatus Lane)> whileBusy = [];
        var inner = factory.Responder;
        factory.Responder = script =>
        {
            if (whileBusy.Count == 0) whileBusy = live.BusyLanes();
            return inner(script);
        };

        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);

        Assert.That(whileBusy, Is.Not.Empty, "a busy lane has to be visible to another process");
        Assert.Multiple(() =>
        {
            Assert.That(whileBusy[0].Process.ProcessName, Is.EqualTo("automata-runner"));
            Assert.That(whileBusy[0].Process.TargetName, Is.EqualTo("Batch"));
            Assert.That(whileBusy[0].Lane.TaskName, Is.EqualTo("Batch"));
            Assert.That(whileBusy[0].Lane.CurrentStepLabel, Is.EqualTo("Go"),
                "the step in flight is the point of a live strip, not just the task");
        });
        Assert.That(live.List(), Is.Empty,
            "and the record goes when the run does, so a watcher sees the work stop at once");
    }

    [Test]
    public async Task StatusReportsWhatIsRunningRightNowBeforeAnythingHistorical()
    {
        SeedTask("Nightly", "Batch", Nav());
        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);
        output.GetStringBuilder().Clear();

        // Seeded AFTER that run, on purpose: a finished run clears its OWN monitor file on the way
        // out, which shares this process id — so seeding first would have it swept away and the
        // test would prove the opposite of what it means to.
        live.Publish(new LiveLanes
        {
            ProcessId = Environment.ProcessId,
            ProcessStartedUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            ProcessName = "automata-runner",
            TargetName = "Nightly",
            MaxConcurrency = 3,
            UpdatedUtc = clock.UtcNow,
            Lanes = [new LaneStatus("lane-1", "default", true, "r1", "Wolf Tshirts", "Click Images", clock.UtcNow)],
        });

        await Dispatcher().DispatchAsync(["status"]);

        Assert.That(Written, Does.Contain("Running now"));
        Assert.That(Written, Does.Contain("Click Images"), "the step in flight, not just the task");
        Assert.That(Written, Does.Contain("up to 3 lane(s)"));
        Assert.That(Written.IndexOf("Running now", StringComparison.Ordinal),
            Is.LessThan(Written.IndexOf("Recent runs", StringComparison.Ordinal)),
            "what is happening now comes before what has already happened");
    }

    [Test]
    public async Task StatusListsParkedRunsBeforeRecentOnes()
    {
        SeedTask("Nightly", "Batch", LongWait("w"), Nav());
        await Dispatcher().DispatchAsync(["run", "--task", "Batch"]);
        output.GetStringBuilder().Clear();

        await Dispatcher().DispatchAsync(["status"]);

        Assert.That(Written, Does.Contain("Parked, waiting to resume"));
        Assert.That(Written.IndexOf("Parked, waiting", StringComparison.Ordinal),
            Is.LessThan(Written.IndexOf("Recent runs", StringComparison.Ordinal)),
            "what is still owed comes first");
    }
}

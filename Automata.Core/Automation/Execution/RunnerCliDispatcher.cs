using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Settings;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;

namespace Automata.Core.Automation.Execution;

/// <summary>What the process exits with. Scripted callers branch on these, so they are part of the contract.</summary>
public static class RunnerExitCode
{
    public const int Success = 0;
    public const int RunFailed = 1;
    public const int Fault = 2;
    public const int BadArguments = 3;
}

/// <summary>
/// Everything <c>automata-runner</c> actually does.
/// <para>
/// It lives in Automata.Core, not in the runner executable, for one reason: the executable has to
/// be <c>net10.0-windows</c> to host a browser, and the test project is plain <c>net10.0</c>. Keeping
/// the dispatch logic here means the CLI surface, the argument handling and the run orchestration
/// are all unit-testable against fakes, and the exe stays a one-line shim — the same thin-host,
/// fat-core split the WPF app already uses.
/// </para>
/// </summary>
public sealed class RunnerCliDispatcher
{
    private readonly CollectionStore collections;
    private readonly RunStore runs;
    private readonly WorkflowEngine engine;
    private readonly AutomataSettingsStore settings;
    private readonly IBrowserSurfaceFactory laneFactory;
    private readonly TextWriter output;
    private readonly ScheduleStore schedule;
    private readonly ParkedRunStore parked;
    private readonly LiveLaneStore live;
    private readonly IClock clock;
    private readonly IScheduledTaskRegistrar? registrar;
    private readonly DemoSeeder? demos;

    public RunnerCliDispatcher(
        CollectionStore collections,
        RunStore runs,
        WorkflowEngine engine,
        AutomataSettingsStore settings,
        IBrowserSurfaceFactory laneFactory,
        TextWriter output,
        ScheduleStore? schedule = null,
        IClock? clock = null,
        IScheduledTaskRegistrar? registrar = null,
        ParkedRunStore? parked = null,
        LiveLaneStore? live = null,
        DemoSeeder? demos = null)
    {
        this.collections = collections;
        this.runs = runs;
        this.engine = engine;
        this.settings = settings;
        this.laneFactory = laneFactory;
        this.output = output;
        this.schedule = schedule ?? new ScheduleStore();
        this.clock = clock ?? new SystemClock();
        this.registrar = registrar;
        this.parked = parked ?? new ParkedRunStore();
        this.live = live ?? new LiveLaneStore();
        this.demos = demos;
    }

    public async Task<int> DispatchAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage();
            return args.Length == 0 ? RunnerExitCode.BadArguments : RunnerExitCode.Success;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "run" => await RunAsync(args, ct),
                "tick" => await TickAsync(ct),
                "schedule" => await ScheduleCommandAsync(args, ct),
                "install" => await InstallAsync(args, ct),
                "uninstall" => await UninstallAsync(ct),
                "status" => Status(),
                "demos" => DemosCommand(args),
                _ => Unknown(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Cancelled.");
            return RunnerExitCode.Fault;
        }
        catch (Exception ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return RunnerExitCode.Fault;
        }
    }

    // ---- run -------------------------------------------------------------------------------

    private async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var taskRef = Option(args, "--task");
        var collectionRef = Option(args, "--collection");

        if (taskRef == null && collectionRef == null)
        {
            output.WriteLine("error: run needs --task <id|name> or --collection <id|name>");
            return RunnerExitCode.BadArguments;
        }
        if (taskRef != null && collectionRef != null)
        {
            output.WriteLine("error: give --task or --collection, not both");
            return RunnerExitCode.BadArguments;
        }

        var tasks = new List<TaskDefinition>();
        string targetName;
        RunTargetKind kind;

        if (taskRef != null)
        {
            var task = FindTask(taskRef);
            if (task == null)
            {
                output.WriteLine($"error: no task matching '{taskRef}'");
                return RunnerExitCode.BadArguments;
            }
            tasks.Add(task);
            targetName = task.Name;
            kind = RunTargetKind.Task;
        }
        else
        {
            var collection = FindCollection(collectionRef!);
            if (collection == null)
            {
                output.WriteLine($"error: no collection matching '{collectionRef}'");
                return RunnerExitCode.BadArguments;
            }
            tasks.AddRange(collections.LoadTasks(collection.Id));
            targetName = collection.Name;
            kind = RunTargetKind.Collection;
            if (tasks.Count == 0)
            {
                output.WriteLine($"'{collection.Name}' has no tasks.");
                return RunnerExitCode.Success;
            }
        }

        var run = runs.CreateRun(kind, tasks[0].CollectionId, targetName, trigger: "manual");
        output.WriteLine($"Run {run.RunId[..8]} — {targetName}");

        return await RunTaskListAsync(
            run, kind, targetName, tasks, alreadyPassed: 0, totalTasks: tasks.Count,
            resumeFirst: null, resumeCount: 0, ct);
    }

    /// <summary>
    /// Runs a list of tasks under one run record, and is the single path both a fresh run and a
    /// resumed one take.
    /// <para>
    /// <paramref name="resumeFirst"/> continues the first task in the list from a checkpoint;
    /// <paramref name="alreadyPassed"/> and <paramref name="totalTasks"/> carry the tallies from
    /// before the park, so a resumed collection still reports "3/4 task(s) passed" rather than
    /// counting only what ran after the wait.
    /// </para>
    /// </summary>
    private async Task<int> RunTaskListAsync(
        RunManifest run,
        RunTargetKind kind,
        string targetName,
        List<TaskDefinition> tasks,
        int alreadyPassed,
        int totalTasks,
        ParkCheckpoint? resumeFirst,
        int resumeCount,
        CancellationToken ct)
    {
        var global = settings.Load();

        // The lanes this run opens are the interesting ones, and nobody can see them: this process
        // is headless and usually running unattended. The monitor publishes them where the desktop
        // app can read them, and takes its file away again when the run ends.
        using var monitor = new LaneMonitor(live, "automata-runner")
        {
            TargetName = targetName,
            RunId = run.RunId,
        };

        // One pool for the whole run, sized by the resolved ceiling. That is the single place the
        // machine-level concurrency setting is applied; a for-each may only ask for less.
        var ceiling = EngineSettingsResolver.Resolve(global).MaxConcurrency;
        await using var pool = new BrowserLanePool(laneFactory, ceiling, onChanged: monitor.OnChanged);

        var passed = alreadyPassed;
        var stopped = false;

        for (var index = 0; index < tasks.Count; index++)
        {
            if (ct.IsCancellationRequested) break;
            var task = tasks[index];

            var collection = collections.GetCollection(task.CollectionId);
            var resolved = EngineSettingsResolver.Resolve(global, collection?.Settings, task.Settings);
            var options = new ReplayOptions
            {
                ResolveForStep = step => EngineSettingsResolver.Resolve(
                    global, collection?.Settings, task.Settings, step.Settings),
                // The same clock the scheduler reads. Parking hinges on "how long is left", and a
                // run that measured a wait against a different clock from the tick that resumes it
                // would park until a moment the tick never agrees has arrived.
                Clock = () => clock.UtcNow,
            };

            output.WriteLine($"  {task.Name}…");
            await using var lease = await pool.AcquireAsync(
                resolved.BrowserProfile ?? "default", run.RunId, task.Name, ct);

            var success = false;
            ParkCheckpoint? checkpoint = null;
            var outputs = new Dictionary<string, Dictionary<string, string>>();

            await foreach (var evt in engine.RunAsync(
                task, options, lease.Surface, ct, pool, index == 0 ? resumeFirst : null))
            {
                runs.AppendEvent(run.RunId, task.Id, new { kind = evt.GetType().Name, detail = evt.ToString() });
                switch (evt)
                {
                    case StepEvent.StepStarted started:
                        lease.Describe(started.Label);
                        break;
                    case StepEvent.StepCompleted { ExtractedText: not null } done:
                        outputs[done.StepId] = new Dictionary<string, string> { ["text"] = done.ExtractedText! };
                        break;
                    case StepEvent.Log line:
                        output.WriteLine($"    {line.Message}");
                        break;
                    case StepEvent.RunParked p:
                        checkpoint = p.Checkpoint;
                        break;
                    case StepEvent.RunCompleted completed:
                        success = completed.Success;
                        output.WriteLine($"    {(completed.Success ? "ok" : "FAILED")} — {completed.Summary}");
                        break;
                }
            }

            if (outputs.Count > 0) runs.SaveOutputs(run.RunId, task.Id, outputs);

            if (checkpoint != null)
            {
                // The run stays OPEN — its manifest is not completed, because it has not
                // finished. The lane is released by leaving this scope, which is the whole point.
                parked.Save(new ParkedRun
                {
                    RunId = run.RunId,
                    Target = kind,
                    TargetName = targetName,
                    Trigger = run.Trigger,
                    TaskId = task.Id,
                    TaskName = task.Name,
                    CollectionId = task.CollectionId,
                    RemainingTaskIds = tasks.Skip(index + 1).Select(t => t.Id).ToList(),
                    TasksPassed = passed,
                    TotalTasks = totalTasks,
                    ParkedAtUtc = clock.UtcNow,
                    ResumeCount = resumeCount,
                    Checkpoint = checkpoint,
                });
                output.WriteLine(
                    $"Parked {run.RunId[..8]} — resumes {checkpoint.ResumeAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}. " +
                    "No browser is held meanwhile; the next tick after that time carries on.");
                return RunnerExitCode.Success;
            }

            if (success) passed++;
            else if (!resolved.ContinueOnTaskError)
            {
                output.WriteLine($"    stopping: continue-on-task-error is off for '{targetName}'");
                stopped = true;
                break;
            }
        }

        var summary = $"{passed}/{totalTasks} task(s) passed" + (stopped ? " (stopped early)" : "");
        runs.CompleteRun(run.RunId, passed == totalTasks, summary);
        output.WriteLine(summary);
        return passed == totalTasks ? RunnerExitCode.Success : RunnerExitCode.RunFailed;
    }

    /// <summary>
    /// Picks a parked run back up: the task it stopped in, from the step after its wait, then
    /// whatever tasks were still to come.
    /// </summary>
    private async Task<int> ResumeParkedAsync(ParkedRun entry, CancellationToken ct)
    {
        var run = runs.GetRun(entry.RunId);
        if (run == null)
        {
            // Its run directory is gone — deleted, or an older Runs folder cleared out. There is
            // nothing left to continue, and keeping the checkpoint would retry it every tick.
            parked.Remove(entry.RunId);
            output.WriteLine($"Dropping parked run {Short(entry.RunId)}: its run record no longer exists.");
            return RunnerExitCode.Success;
        }

        var task = collections.GetTask(entry.TaskId);
        if (task == null)
        {
            parked.Remove(entry.RunId);
            runs.CompleteRun(entry.RunId, false, $"'{entry.TaskName}' was deleted while the run was parked.");
            output.WriteLine($"Dropping parked run {Short(entry.RunId)}: '{entry.TaskName}' no longer exists.");
            return RunnerExitCode.RunFailed;
        }

        // Removed BEFORE the resumed run starts, so a task that parks a second time writes a fresh
        // checkpoint rather than racing the old one — and so a crash mid-resume cannot leave a
        // checkpoint that resumes the same wait forever.
        parked.Remove(entry.RunId);

        var remaining = new List<TaskDefinition> { task };
        foreach (var id in entry.RemainingTaskIds)
        {
            var next = collections.GetTask(id);
            if (next != null) remaining.Add(next);
            else output.WriteLine($"  (a task queued after '{task.Name}' has since been deleted — skipping it)");
        }

        output.WriteLine($"Resuming {Short(entry.RunId)} — {entry.TargetName}, after {entry.Checkpoint.Reason}");
        return await RunTaskListAsync(
            run, entry.Target, entry.TargetName, remaining,
            entry.TasksPassed, entry.TotalTasks, entry.Checkpoint, entry.ResumeCount + 1, ct);
    }

    private static string Short(string id) => id.Length >= 8 ? id[..8] : id;

    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        return $"{span.TotalHours:0.#}h";
    }

    // ---- tick ------------------------------------------------------------------------------

    /// <summary>
    /// One scheduler heartbeat: run whatever is due, follow whatever that starts, and write down
    /// when each entry is next expected.
    /// <para>
    /// This is the only thing Windows Task Scheduler ever has to invoke. All the cron, interval and
    /// after-this-finishes reasoning happens here, in-process — which is what lets Automata express
    /// schedules <c>schtasks</c> has no vocabulary for (cron, "after the ingest finishes", chains)
    /// while the registered task stays a dumb "run this exe every N minutes".
    /// </para>
    /// </summary>
    private async Task<int> TickAsync(CancellationToken ct)
    {
        var anyFailed = false;

        // Parked runs first, and unconditionally: a run that checkpointed on a long wait is
        // already in flight and half-finished, so finishing it matters more than starting
        // anything new — and it must not depend on there being a schedule at all, since a
        // manually started run can park just as easily as a scheduled one.
        var resumed = 0;
        foreach (var entry in parked.Due(clock.UtcNow))
        {
            if (ct.IsCancellationRequested) break;
            if (await ResumeParkedAsync(entry, ct) != RunnerExitCode.Success) anyFailed = true;
            resumed++;
        }

        var entries = schedule.Load();
        if (entries.Count == 0)
        {
            output.WriteLine(resumed == 0
                ? "Nothing scheduled."
                : $"Resumed {resumed} parked run(s); nothing scheduled.");
            return anyFailed ? RunnerExitCode.RunFailed : RunnerExitCode.Success;
        }

        var started = new HashSet<string>(StringComparer.Ordinal);
        var ran = 0;

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;

            var verdict = TriggerEvaluator.Evaluate(entry, clock);
            // Written down even when not due, so a later tick knows what it missed.
            entry.NextDueUtc = verdict.NextUtc;
            if (!verdict.Due) continue;

            ran += await RunEntryChainAsync(entry, entries, started, ct, failed => anyFailed |= failed);
        }

        schedule.Save(entries);
        var resumedNote = resumed == 0 ? "" : $"Resumed {resumed} parked run(s). ";
        output.WriteLine(resumedNote + (ran == 0 ? "Nothing due." : $"Ran {ran} scheduled item(s)."));
        return anyFailed ? RunnerExitCode.RunFailed : RunnerExitCode.Success;
    }

    /// <summary>Runs an entry, then whatever its outcome starts, in order.</summary>
    private async Task<int> RunEntryChainAsync(
        ScheduleEntry entry,
        List<ScheduleEntry> all,
        HashSet<string> started,
        CancellationToken ct,
        Action<bool> reportFailure)
    {
        var ran = 0;
        var queue = new Queue<ScheduleEntry>();
        queue.Enqueue(entry);

        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var current = queue.Dequeue();
            // An entry runs at most once per tick, so a cycle exhausts itself rather than looping.
            if (!started.Add(current.Id)) continue;

            output.WriteLine($"Due: {current.Name}");
            var code = await RunTargetAsync(current, ct);
            var succeeded = code == RunnerExitCode.Success;
            ran++;
            reportFailure(!succeeded);

            current.LastRunUtc = clock.UtcNow;
            current.LastOutcome = succeeded ? "passed" : "failed";

            foreach (var dependent in TriggerEvaluator.Dependents(all, current.Id, succeeded))
            {
                output.WriteLine($"  starts: {dependent.Name}");
                queue.Enqueue(dependent);
            }
        }
        return ran;
    }

    private Task<int> RunTargetAsync(ScheduleEntry entry, CancellationToken ct) =>
        RunAsync(
            entry.Target == ScheduleTargetKind.Task
                ? ["run", "--task", entry.TargetId]
                : ["run", "--collection", entry.TargetId],
            ct);

    // ---- schedule ---------------------------------------------------------------------------

    private async Task<int> ScheduleCommandAsync(string[] args, CancellationToken ct)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        var entries = schedule.Load();

        switch (sub)
        {
            case "list":
            {
                if (entries.Count == 0)
                {
                    output.WriteLine("Nothing scheduled.");
                    return RunnerExitCode.Success;
                }
                foreach (var entry in entries)
                {
                    var verdict = TriggerEvaluator.Evaluate(entry, clock);
                    output.WriteLine($"  {entry.Id[..Math.Min(8, entry.Id.Length)]}  {entry.Name}");
                    output.WriteLine($"      {verdict.Reason}" +
                        (entry.LastOutcome == null ? "" : $"; last run {entry.LastOutcome}"));
                }
                return RunnerExitCode.Success;
            }

            case "add":
            {
                var target = Option(args, "--collection") ?? Option(args, "--task");
                var isTask = Option(args, "--task") != null;
                var cron = Option(args, "--cron");
                var every = Option(args, "--every-minutes");
                var after = Option(args, "--after");

                if (target == null)
                {
                    output.WriteLine("error: schedule add needs --collection <id|name> or --task <id|name>");
                    return RunnerExitCode.BadArguments;
                }
                if (cron == null && every == null && after == null)
                {
                    output.WriteLine("error: give --cron \"<expr>\", --every-minutes <n>, or --after <entry-id>");
                    return RunnerExitCode.BadArguments;
                }

                string targetId, name;
                if (isTask)
                {
                    var task = FindTask(target);
                    if (task == null) { output.WriteLine($"error: no task matching '{target}'"); return RunnerExitCode.BadArguments; }
                    (targetId, name) = (task.Id, task.Name);
                }
                else
                {
                    var collection = FindCollection(target);
                    if (collection == null) { output.WriteLine($"error: no collection matching '{target}'"); return RunnerExitCode.BadArguments; }
                    (targetId, name) = (collection.Id, collection.Name);
                }

                var trigger = new TriggerDefinition();
                if (cron != null)
                {
                    // Refused here rather than silently never firing, which is the worst failure
                    // mode a schedule can have.
                    if (!CronSchedule.TryParse(cron, out _, out var error))
                    {
                        output.WriteLine($"error: {error}");
                        return RunnerExitCode.BadArguments;
                    }
                    trigger.Kind = TriggerKind.Cron;
                    trigger.CronExpression = cron;
                    trigger.TimeZoneId = Option(args, "--timezone");
                }
                else if (every != null)
                {
                    if (!int.TryParse(every, out var minutes) || minutes <= 0)
                    {
                        output.WriteLine("error: --every-minutes needs a positive whole number");
                        return RunnerExitCode.BadArguments;
                    }
                    trigger.Kind = TriggerKind.Interval;
                    trigger.IntervalSeconds = minutes * 60;
                    trigger.AnchorUtc = clock.UtcNow;
                }
                else
                {
                    if (entries.All(e => e.Id != after))
                    {
                        output.WriteLine($"error: no schedule entry with id '{after}'");
                        return RunnerExitCode.BadArguments;
                    }
                    trigger.Kind = TriggerKind.AfterEntry;
                    trigger.AfterEntryId = after;
                }

                var entry = new ScheduleEntry
                {
                    Name = name,
                    Target = isTask ? ScheduleTargetKind.Task : ScheduleTargetKind.Collection,
                    TargetId = targetId,
                    Triggers = [trigger],
                };
                entry.NextDueUtc = TriggerEvaluator.Evaluate(entry, clock).NextUtc;
                schedule.Upsert(entry);

                output.WriteLine($"Scheduled '{name}' as {entry.Id[..8]}.");
                output.WriteLine($"  {TriggerEvaluator.Evaluate(entry, clock).Reason}");
                return RunnerExitCode.Success;
            }

            case "remove":
            case "enable":
            case "disable":
            {
                var id = args.Length > 2 ? args[2] : null;
                var entry = id == null ? null : entries.FirstOrDefault(e => e.Id.StartsWith(id, StringComparison.Ordinal));
                if (entry == null)
                {
                    output.WriteLine($"error: no schedule entry matching '{id}'");
                    return RunnerExitCode.BadArguments;
                }
                if (sub == "remove")
                {
                    schedule.Remove(entry.Id);
                    output.WriteLine($"Removed '{entry.Name}'.");
                }
                else
                {
                    entry.Enabled = sub == "enable";
                    schedule.Save(entries);
                    output.WriteLine($"{(entry.Enabled ? "Enabled" : "Disabled")} '{entry.Name}'.");
                }
                return RunnerExitCode.Success;
            }

            default:
                output.WriteLine($"error: unknown schedule command '{sub}'");
                return RunnerExitCode.BadArguments;
        }
    }

    // ---- install / uninstall ------------------------------------------------------------------

    private async Task<int> InstallAsync(string[] args, CancellationToken ct)
    {
        if (registrar == null)
        {
            output.WriteLine("error: task registration is not available in this build");
            return RunnerExitCode.Fault;
        }
        var minutes = int.TryParse(Option(args, "--interval-minutes"), out var m) && m > 0 ? m : 5;
        var result = await registrar.InstallAsync(minutes, ct);
        output.WriteLine(result.Report);

        // A refusal is never dressed up as success. Reporting "installed" over a scheduler that
        // said no produces the one failure this whole feature exists to avoid: a schedule the user
        // believes in that fires nothing.
        if (!result.Succeeded)
        {
            output.WriteLine("error: nothing is scheduled — the heartbeat was NOT registered.");
            return RunnerExitCode.Fault;
        }

        output.WriteLine(
            "Registered to run only while you are logged on. WebView2 cannot render in session 0, " +
            "so an unattended task would start and then fail to drive a browser.");
        return RunnerExitCode.Success;
    }

    private async Task<int> UninstallAsync(CancellationToken ct)
    {
        if (registrar == null)
        {
            output.WriteLine("error: task registration is not available in this build");
            return RunnerExitCode.Fault;
        }
        var result = await registrar.UninstallAsync(ct);
        output.WriteLine(result.Report);
        return result.Succeeded ? RunnerExitCode.Success : RunnerExitCode.Fault;
    }

    // ---- demos -----------------------------------------------------------------------------

    /// <summary>
    /// The generated examples. Seeding is safe to run at any time and never touches an example the
    /// user has edited; regenerating only overwrites the ones named on the command line, which is
    /// the CLI's version of being asked.
    /// </summary>
    private int DemosCommand(string[] args)
    {
        if (demos == null)
        {
            output.WriteLine("error: demo generation is not available in this build");
            return RunnerExitCode.Fault;
        }

        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                foreach (var status in demos.Survey())
                    output.WriteLine($"{Describe(status.State),-10} {status.Name}  [{status.Key}]");
                output.WriteLine($"Pages: {demos.RootPath}");
                return RunnerExitCode.Success;
            }

            case "seed":
                Report(demos.SeedMissing());
                return RunnerExitCode.Success;

            case "regenerate":
            {
                var revertAll = args.Any(a => a.Equals("--revert-all", StringComparison.OrdinalIgnoreCase));
                var resolutions = new Dictionary<string, DemoResolution>(StringComparer.Ordinal);

                foreach (var status in demos.Survey())
                {
                    if (status.State != DemoState.Edited) continue;
                    if (revertAll || Named(args, "--revert").Contains(status.Key))
                        resolutions[status.Key] = DemoResolution.Revert;
                    else if (Named(args, "--clone").Contains(status.Key))
                        resolutions[status.Key] = DemoResolution.Clone;
                }

                Report(demos.Regenerate(resolutions));
                return RunnerExitCode.Success;
            }

            default:
                output.WriteLine($"error: unknown demos command '{sub}'");
                return RunnerExitCode.BadArguments;
        }
    }

    private static string Describe(DemoState state) => state switch
    {
        DemoState.Missing => "missing",
        DemoState.Current => "current",
        DemoState.Stale => "stale",
        _ => "EDITED",
    };

    /// <summary>Every value given for a repeatable option, e.g. <c>--revert a --revert b</c>.</summary>
    private static HashSet<string> Named(string[] args, string option)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase)) found.Add(args[i + 1]);
        return found;
    }

    private void Report(DemoSeedReport report)
    {
        output.WriteLine($"{report.PagesWritten.Count} page(s) written to {demos!.RootPath}");
        Line("added", report.Added);
        Line("refreshed", report.Refreshed);
        Line("reverted", report.Reverted);
        Line("cloned", report.Cloned);
        Line("kept (edited, left alone)", report.Kept);

        var edited = report.Before.Count(s => s.State == DemoState.Edited);
        var untouched = edited - report.Reverted.Count - report.Cloned.Count;
        if (untouched > 0)
        {
            output.WriteLine(
                $"{untouched} edited example(s) were left as they are. Name one with " +
                "--revert <key> to restore the original, or --clone <key> to keep yours and add " +
                "the original beside it.");
        }

        void Line(string label, IReadOnlyList<string> names)
        {
            if (names.Count > 0) output.WriteLine($"  {label}: {string.Join(", ", names)}");
        }
    }

    // ---- status ----------------------------------------------------------------------------

    private int Status()
    {
        // What is happening RIGHT NOW, before anything historical. These lanes belong to other
        // processes — very often a scheduled run this one knows nothing about — which is exactly
        // why they are worth reporting.
        var busy = live.BusyLanes();
        if (busy.Count > 0)
        {
            output.WriteLine("Running now:");
            foreach (var perProcess in busy.GroupBy(x => x.Process.ProcessId))
            {
                var owner = perProcess.First().Process;
                output.WriteLine(
                    $"  {owner.ProcessName} (pid {owner.ProcessId}) — {owner.TargetName ?? "?"}, " +
                    $"up to {owner.MaxConcurrency} lane(s) at a time");
                foreach (var (_, lane) in perProcess)
                {
                    var age = lane.StartedUtc == null ? "" : $"  ({Describe(clock.UtcNow - lane.StartedUtc.Value)})";
                    output.WriteLine(
                        $"    [{lane.LaneId} · {lane.ProfileKey}]  {lane.TaskName ?? "?"}" +
                        (lane.CurrentStepLabel == null ? "" : $" — {lane.CurrentStepLabel}") + age);
                }
            }
            output.WriteLine("");
        }

        // Parked runs next: they are the ones still owed something, and the only ones whose
        // absence from this list would leave someone wondering where their run went.
        var waiting = parked.List();
        if (waiting.Count > 0)
        {
            output.WriteLine("Parked, waiting to resume:");
            foreach (var entry in waiting)
            {
                var due = entry.ResumeAtUtc.ToLocalTime();
                output.WriteLine(
                    $"  {due:yyyy-MM-dd HH:mm}  {entry.TargetName}  — parked on " +
                    $"'{entry.Checkpoint.StepLabel}' in '{entry.TaskName}', {entry.Checkpoint.Reason}" +
                    (entry.ResumeAtUtc <= clock.UtcNow ? "  (due now — waiting for the next tick)" : ""));
            }
            output.WriteLine("");
        }

        var recent = runs.ListRuns(limit: 10);
        if (recent.Count == 0)
        {
            output.WriteLine("No runs recorded yet.");
            return RunnerExitCode.Success;
        }

        output.WriteLine("Recent runs (newest first):");
        foreach (var run in recent)
        {
            var state = run.Success == null ? "running" : run.Success.Value ? "passed" : "failed";
            output.WriteLine(
                $"  {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {state,-7}  {run.TargetName}" +
                (string.IsNullOrWhiteSpace(run.Summary) ? "" : $"  — {run.Summary}"));
        }
        return RunnerExitCode.Success;
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>Ids are exact; names are matched case-insensitively, because nobody types a GUID.</summary>
    private TaskDefinition? FindTask(string reference)
    {
        var byId = collections.GetTask(reference);
        if (byId != null) return byId;
        return collections.LoadCollections()
            .SelectMany(c => collections.LoadTasks(c.Id))
            .FirstOrDefault(t => string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase));
    }

    private Collection? FindCollection(string reference)
    {
        var byId = collections.GetCollection(reference);
        if (byId != null) return byId;
        return collections.LoadCollections()
            .FirstOrDefault(c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHelp(string arg) =>
        arg is "--help" or "-h" or "/?" or "help";

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private int Unknown(string command)
    {
        output.WriteLine($"error: unknown command '{command}'");
        WriteUsage();
        return RunnerExitCode.BadArguments;
    }

    private void WriteUsage() => output.WriteLine("""
        automata-runner — runs Automata tasks without the desktop app.

          run --task <id|name>          run one task
          run --collection <id|name>    run every task in a collection, in order
          tick                          resume parked runs and run whatever is due (what Task Scheduler invokes)
          status                        lanes running now, parked runs, then the ten most recent

          schedule list                 what is scheduled, and when each is next due
          schedule add --collection <id|name> --cron "0 9 * * *" [--timezone <id>]
          schedule add --task <id|name> --every-minutes <n>
          schedule add --collection <id|name> --after <entry-id>
          schedule enable|disable|remove <entry-id>

          install [--interval-minutes 5]   register the tick with Windows Task Scheduler
          uninstall                        remove it

          demos list                    the generated examples, and which have been edited
          demos seed                    write any example that is missing; refresh untouched ones
          demos regenerate [--revert <key>...] [--clone <key>...] [--revert-all]
                                        rebuild the examples; edited ones are kept unless named
          --help                        this text

        Exit codes: 0 success, 1 a run failed, 2 fault, 3 bad arguments.

        Browser lanes need an interactive session: WebView2 cannot render in session 0, so a
        scheduled task must be registered to run only when the user is logged on.

        A wait longer than its step's park-after threshold (15 minutes by default) checkpoints the
        run and releases its browser rather than holding one idle; the next tick after the wait
        ends picks it up. Parking resets the page to the task's start URL, so a task that must keep
        what it did before the wait should set that threshold to 0 and hold the browser instead.
        """);
}

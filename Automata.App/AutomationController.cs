using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Automata.Core.Automation;
using Automata.Core.Automation.Logging;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Recording;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Replay;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Settings;
using Automata.Core.Automation.Storage;
using Automata.Core.Operator;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Automata.App;

/// <summary>
/// Everything the sidebar's collection/task/step UI can ask for: store CRUD, recording,
/// replay (run / dry run / validate), pause-continue, and zip import/export. Lives beside
/// MainWindow so the window class stays a thin bridge.
/// </summary>
public sealed class AutomationController
{
    private readonly CollectionStore store;
    private readonly ArchiveService archive;
    private readonly WorkflowEngine engine;
    private readonly AutomataSettingsStore settingsStore;
    private readonly DatasetStore datasets;
    private readonly RunStore runs;
    private readonly ScheduleStore schedule;
    private readonly ParkedRunStore parkedRuns;
    private readonly LiveLaneStore liveLanes;
    private readonly DemoSeeder demos;
    private readonly IClock clock;

    /// <summary>
    /// The run a collection is currently working through, so its tasks land under ONE run record
    /// rather than one each. Null when a single task is being run on its own.
    /// </summary>
    private RunManifest? currentRun;
    private readonly FlowAuthoringService authoring;

    /// <summary>
    /// The last drafted feature, held so Insert saves what the user actually reviewed rather than
    /// asking the model again and getting something subtly different.
    /// </summary>
    private FlowDraft? pendingDraft;
    private readonly Func<IBrowserSurface?> targetSurface;
    private readonly Func<CoreWebView2?> targetCore;
    private readonly Func<string, Task> execPanelScript;
    private readonly Func<string, Task> logAsync;

    private bool recording;
    private readonly List<RecorderEvent> recorded = [];
    private CancellationTokenSource? replayCts;
    private ReplayControl? replayControl;
    private bool runActive;

    /// <summary>Set by Cancel while a collection run is in progress — checked between tasks so
    /// Cancel stops the whole collection, not just whichever task happens to be running.</summary>
    private bool collectionCancelRequested;

    /// <summary>Set while a "record at this gap" run is armed and waiting for Stop — where the
    /// captured step(s) should be spliced once recording stops, and whether the underlying run is
    /// still genuinely suspended (mid-tree, parked on <see cref="ReplayControl.WaitAsync"/> and
    /// resumable via Continue) versus already finished (the gap was the last slot — RunCompleted
    /// already fired, so nothing is left running to resume or cancel).</summary>
    private (string TaskId, string? ParentStepId, int Index, bool RunStillSuspended)? pendingGapInsert;

    private readonly record struct GapTarget(string? ParentStepId, int Index, string? PauseBeforeStepId);

    public AutomationController(
        CollectionStore store,
        ArchiveService archive,
        WorkflowEngine engine,
        AutomataSettingsStore settingsStore,
        DatasetStore datasets,
        RunStore runs,
        ScheduleStore schedule,
        ParkedRunStore parkedRuns,
        LiveLaneStore liveLanes,
        DemoSeeder demos,
        IClock clock,
        FlowAuthoringService authoring,
        Func<IBrowserSurface?> targetSurface,
        Func<CoreWebView2?> targetCore,
        Func<string, Task> execPanelScript,
        Func<string, Task> logAsync)
    {
        this.store = store;
        this.archive = archive;
        this.engine = engine;
        this.settingsStore = settingsStore;
        this.datasets = datasets;
        this.runs = runs;
        this.schedule = schedule;
        this.parkedRuns = parkedRuns;
        this.liveLanes = liveLanes;
        this.demos = demos;
        this.clock = clock;
        this.authoring = authoring;
        this.targetSurface = targetSurface;
        this.targetCore = targetCore;
        this.execPanelScript = execPanelScript;
        this.logAsync = logAsync;
    }

    // ---- panel message dispatch ----------------------------------------------------------------

    /// <summary>Handle one sidebar message. Returns false for actions this controller doesn't own.</summary>
    public async Task<bool> TryHandlePanelMessageAsync(string action, JsonNode msg)
    {
        switch (action)
        {
            case "getState":
                // First load seeds the generated examples, so a new user has something that works
                // to run and read before building anything. It is a no-op on every later launch,
                // and it never overwrites an example somebody has edited.
                await SeedDemosOnceAsync();
                await PushStateAsync();
                return true;

            case "surveyDemos":
                await PushDemoSurveyAsync();
                return true;

            case "pickHarvest":
                await ArmHarvestPickAsync(Str(msg, "mode") ?? "row", Str(msg, "itemSelector") ?? "");
                return true;

            case "cancelHarvestPick":
            {
                var surface = targetCore();
                if (surface != null)
                    await TryEvalAsync(surface, "window.__automataRecorder && window.__automataRecorder.cancelPick()");
                return true;
            }

            case "regenerateDemos":
            {
                var report = demos.Regenerate();
                await logAsync(SummariseDemoRegeneration(report));
                await PushStateAsync();
                await PushDemoSurveyAsync();
                return true;
            }

            case "createCollection":
                store.CreateCollection(Str(msg, "name") ?? "New collection");
                await PushStateAsync();
                return true;

            case "renameCollection":
            {
                var collection = store.GetCollection(Str(msg, "id") ?? "");
                if (collection != null)
                {
                    collection.Name = Str(msg, "name") ?? collection.Name;
                    store.SaveCollection(collection);
                }
                await PushStateAsync();
                return true;
            }

            case "deleteCollection":
                store.DeleteCollection(Str(msg, "id") ?? "");
                await PushStateAsync();
                return true;

            case "saveCollectionSettings":
            {
                var scopedId = Str(msg, "id") ?? "";
                var scoped = store.GetCollection(scopedId);
                if (scoped == null)
                {
                    await logAsync($"⚠ Collection '{scopedId}' not found.");
                    return true;
                }
                scoped.Settings = ParseOverride(msg["settings"]);
                store.SaveCollection(scoped);
                await PushStateAsync();
                return true;
            }

            case "duplicateCollection":
                store.DuplicateCollection(Str(msg, "id") ?? "");
                await PushStateAsync();
                return true;

            case "createTask":
            {
                var task = new TaskDefinition
                {
                    CollectionId = Str(msg, "collectionId") ?? "",
                    Name = Str(msg, "name") ?? "New task",
                };
                store.SaveTask(task);
                await PushStateAsync();
                return true;
            }

            case "saveTask":
            {
                var taskNode = msg["task"];
                var task = taskNode == null
                    ? null
                    : JsonSerializer.Deserialize<TaskDefinition>(taskNode.ToJsonString(), AutomataJson.Options);
                if (task == null)
                {
                    // Nothing was saved, so nothing is known to have changed — re-send the truth
                    // rather than echo a task that may not be what is on disk.
                    await PushStateAsync();
                    return true;
                }
                store.SaveTask(task);
                await PushTaskAsync(task);
                return true;
            }

            case "renameTask":
            {
                var task = store.GetTask(Str(msg, "id") ?? "");
                if (task == null)
                {
                    await PushStateAsync();
                    return true;
                }
                task.Name = Str(msg, "name") ?? task.Name;
                store.SaveTask(task);
                await PushTaskAsync(task);
                return true;
            }

            case "deleteTask":
                store.DeleteTask(Str(msg, "id") ?? "");
                await PushStateAsync();
                return true;

            case "moveTask":
                try { store.MoveTask(Str(msg, "taskId") ?? "", Str(msg, "toCollectionId") ?? ""); }
                catch (InvalidOperationException ex) { await logAsync($"⚠ Move failed: {ex.Message}"); }
                await PushStateAsync();
                return true;

            case "duplicateTask":
                try { store.DuplicateTask(Str(msg, "id") ?? ""); }
                catch (InvalidOperationException ex) { await logAsync($"⚠ Duplicate failed: {ex.Message}"); }
                await PushStateAsync();
                return true;

            case "record":
                await StartRecordingAsync();
                return true;

            case "stopRecord":
                await StopRecordingAsync(msg);
                return true;

            case "runTask":
                _ = RunReplayAsync(Str(msg, "taskId") ?? "", msg["allowRepair"]?.GetValue<bool>() ?? false);
                return true;

            case "runCollection":
                _ = RunCollectionAsync(Str(msg, "collectionId") ?? "", msg["allowRepair"]?.GetValue<bool>() ?? false);
                return true;

            case "recordAtGap":
                _ = RecordAtGapAsync(msg);
                return true;

            case "draftFlow":
                _ = DraftFlowAsync(Str(msg, "description") ?? "");
                return true;

            case "compileFlow":
                await CompileFlowAsync(Str(msg, "featureText") ?? "");
                return true;

            case "insertFlow":
                await InsertDraftAsync();
                return true;

            case "getFeature":
                await PushFeatureAsync(Str(msg, "taskId") ?? "");
                return true;

            case "getRuns":
                await PushRunsAsync();
                return true;

            case "getLanes":
                await PushLanesAsync();
                return true;

            case "openRuns":
                Directory.CreateDirectory(runs.RootPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{runs.RootPath}\"") { UseShellExecute = true });
                await logAsync($"Opened {runs.RootPath}");
                return true;

            case "getSchedule":
                await PushScheduleAsync();
                return true;

            case "saveScheduleEntry":
                await SaveScheduleEntryAsync(msg);
                return true;

            case "deleteScheduleEntry":
            {
                var entryId = Str(msg, "id") ?? "";
                var doomed = schedule.Get(entryId);
                if (doomed == null)
                {
                    await logAsync($"⚠ No schedule entry '{entryId}' to remove.");
                }
                else
                {
                    // Anything waiting on this entry would wait forever, so say so rather than
                    // leaving a chain silently broken.
                    var orphaned = schedule.Load()
                        .Where(e => e.Triggers.Any(t => t.AfterEntryId == entryId))
                        .Select(e => e.Name)
                        .ToList();
                    schedule.Remove(entryId);
                    await logAsync($"Removed schedule '{doomed.Name}'.");
                    if (orphaned.Count > 0)
                        await logAsync(
                            $"⚠ {string.Join(", ", orphaned)} waited for it and will no longer be started by anything.");
                }
                await PushScheduleAsync();
                return true;
            }

            case "getDatasets":
                await PushDatasetsAsync();
                return true;

            case "openDatasets":
                Directory.CreateDirectory(datasets.RootPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{datasets.RootPath}\"") { UseShellExecute = true });
                await logAsync($"Opened {datasets.RootPath}");
                return true;

            case "getSettings":
                await PushSettingsAsync();
                return true;

            case "saveSettings":
            {
                var settings = settingsStore.Load();

                if (Str(msg, "provider") is { } provider
                    && provider is "claude" or "openai" or "gemini" or "kimi")
                {
                    settings.Provider = provider;
                    await logAsync($"LLM provider set to {provider} — used for the next AI run.");
                }

                foreach (var (field, apply) in KeyFields())
                {
                    if (Str(msg, field) is { Length: > 0 } key)
                    {
                        apply(settings, key);
                        await logAsync($"{field} saved (BYO-key) — used for the next AI run.");
                    }
                }
                if (Str(msg, "clearKey") is { } clear)
                {
                    var cleared = clear switch
                    {
                        "claude" => (Action)(() => settings.AnthropicApiKey = null),
                        "openai" => () => settings.OpenAiApiKey = null,
                        "gemini" => () => settings.GeminiApiKey = null,
                        "kimi" => () => settings.KimiApiKey = null,
                        _ => () => { },
                    };
                    cleared();
                    await logAsync($"{clear} key override cleared — falling back to Vault/default credentials.");
                }

                if (msg["borderRadius"] != null)
                    settings.BorderRadius = Math.Clamp(msg["borderRadius"]!.GetValue<int>(), 0, 10);

                if (Str(msg, "theme") is { } theme)
                    settings.Theme = AutomataSettings.Themes.Coerce(theme);

                // The panel always sends a whole override object; ParseOverride collapses one that
                // overrides nothing back to null, so "reset everything to the floor" needs no
                // separate message.
                if (msg["engineDefaults"] is { } engineDefaults)
                {
                    settings.EngineDefaults = ParseOverride(engineDefaults);
                    await logAsync("Global engine defaults updated — applied from the next run.");
                }

                settingsStore.Save(settings);
                await PushSettingsAsync();
                return true;
            }

            case "continueRun":
                replayControl?.Continue();
                return true;

            case "cancelRun":
                replayCts?.Cancel();
                collectionCancelRequested = true;
                // Only tear down recording when it's the gap-recording session tied to THIS run —
                // an ordinary whole-task recording (started via the ● Record button, no run behind
                // it) must survive a Cancel meant for an unrelated AI run or replay.
                if (recording && pendingGapInsert != null) _ = CancelGapRecordingAsync();
                return true;

            case "export":
                await ExportAsync(msg);
                return true;

            case "import":
                await ImportAsync();
                return true;

            case "openCollections":
                Directory.CreateDirectory(store.RootPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{store.RootPath}\"") { UseShellExecute = true });
                await logAsync($"Opened {store.RootPath}");
                return true;

            default:
                return false;
        }
    }

    // ---- recording -----------------------------------------------------------------------------

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task StartRecordingAsync()
    {
        var core = targetCore();
        if (core == null)
        {
            await logAsync("⚠ Target browser isn't ready yet — can't record.");
            return;
        }
        await ArmRecordingCoreAsync(core, "● Recording — perform the actions to capture, then press Stop.", seedNavigate: true);
    }

    /// <summary>Arms the JS recorder on the given pane. Shared by whole-task recording and
    /// record-at-gap (called once a bounded run has parked at the insertion point).</summary>
    private async Task ArmRecordingCoreAsync(CoreWebView2 core, string note, bool seedNavigate)
    {
        recorded.Clear();
        recording = true;
        // Whole-task recording starts a brand-new task, so it seeds where the user is starting
        // from, or replaying it later would begin on the wrong page. Record-at-gap has no such
        // need — the preceding bounded replay already left the pane on the right page, and a
        // seeded Navigate step here would splice in as a bogus mid-task step.
        if (seedNavigate && !string.IsNullOrEmpty(core.Source) && core.Source != "about:blank")
            recorded.Add(new RecorderEvent { Kind = "navigate", Url = core.Source, Ts = NowMs() });

        await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.enable()");
        await execPanelScript("window.ssPanel.onRecordingState(true)");
        await PushRecordedPreviewAsync();
        await logAsync(note);
    }

    private async Task ArmRecordingForInsertAsync(string taskId, string? parentStepId, int index, bool runStillSuspended)
    {
        var core = targetCore();
        if (core == null)
        {
            await logAsync("⚠ Target browser isn't ready yet — can't record.");
            return;
        }
        await ArmRecordingCoreAsync(core, "● Recording at the insertion point — perform the action(s), then press Stop.", seedNavigate: false);
        pendingGapInsert = (taskId, parentStepId, index, runStillSuspended);
    }

    private async Task CancelGapRecordingAsync()
    {
        recording = false;
        var wasRunStillSuspended = pendingGapInsert?.RunStillSuspended ?? false;
        pendingGapInsert = null;

        if (!wasRunStillSuspended)
        {
            // End-of-tree/append: RunEngineAsync already returned (RunCompleted was its terminal
            // event), so nothing else is left to clear the guard/running state — do it here.
            runActive = false;
            await execPanelScript("window.ssPanel.onRunState(false)");
        }
        // Mid-tree: the caller (cancelRun) already cancelled replayCts, which unblocks the still-
        // suspended RunEngineAsync — its own tail finalizes runActive/running once it resumes.

        var core = targetCore();
        if (core != null)
            await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.disable()");
        recorded.Clear();
        await execPanelScript("window.ssPanel.onRecordingState(false)");
    }

    private async Task StopRecordingAsync(JsonNode msg)
    {
        recording = false;
        var core = targetCore();
        if (core != null)
            await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.disable()");

        var steps = RecorderSessionBuilder.Build(recorded);
        var runConcluded = true;

        if (pendingGapInsert is { } gap)
        {
            pendingGapInsert = null;
            if (steps.Count == 0)
                await logAsync("Recording stopped — nothing was captured; nothing inserted.");
            else
                await PushGapRecordedAsync(gap.TaskId, gap.ParentStepId, gap.Index, steps);

            if (gap.RunStillSuspended)
            {
                // Mid-tree: the underlying replay run is still genuinely parked on its pause gate
                // (see ReplayControl.WaitAsync) — leave it there. Continue lets the user keep
                // playing the rest of the (now-updated) task from here, exactly like a persisted
                // PauseForUser step; Cancel aborts it. Nothing to finalize here — running/
                // pausedStepId must stay as they are until the user picks one of those, and
                // RunEngineAsync's own tail concludes things once it resumes.
                runConcluded = false;
            }
            else
            {
                // End-of-tree/append: RunCompleted already fired and RunEngineAsync already
                // returned, so this is the only code left to finalize the guard/running state.
                runActive = false;
            }
        }
        else
        {
            var appendTaskId = Str(msg, "taskId");
            if (steps.Count == 0)
            {
                await logAsync("Recording stopped — nothing was captured.");
            }
            else if (appendTaskId != null && store.GetTask(appendTaskId) is { } existing)
            {
                existing.Steps.AddRange(steps);
                store.SaveTask(existing);
                await logAsync($"Recording stopped — {steps.Count} step(s) appended to '{existing.Name}'.");
            }
            else
            {
                var collectionId = Str(msg, "collectionId") ?? "";
                var task = new TaskDefinition
                {
                    CollectionId = collectionId,
                    Name = Str(msg, "name") ?? "Recorded task",
                    Steps = steps,
                };
                store.SaveTask(task);
                await logAsync($"Recording stopped — saved '{task.Name}' with {steps.Count} step(s). Refine it in the editor.");
            }
        }

        recorded.Clear();
        await execPanelScript("window.ssPanel.onRecordingState(false)");
        // A gap-recording session kept "running" true (see RunEngineAsync) so Cancel stayed
        // available throughout — reset it now that the session is actually over. Idempotent (and
        // a no-op) for an ordinary whole-task recording, which never touched running state. Skipped
        // for a still-suspended mid-tree gap recording — the run legitimately isn't over yet.
        if (runConcluded)
            await execPanelScript("window.ssPanel.onRunState(false)");
        await PushRecordedPreviewAsync();
        await PushStateAsync();
    }

    /// <summary>Raw recorder message from the target pane (already filtered by source tag).</summary>
    public async Task HandleRecorderMessageAsync(JsonNode msg)
    {
        // A pick is not a recording. It arrives on the same channel because it is the same injected
        // script, but it happens while nothing is being recorded — so it is answered before the
        // recording guard below, not after it.
        if (msg["kind"]?.GetValue<string>() == "pick")
        {
            await execPanelScript($"window.ssPanel.onHarvestPick({msg.ToJsonString()})");
            return;
        }

        if (!recording) return;
        try
        {
            var evt = new RecorderEvent
            {
                Kind = Str(msg, "kind") ?? "",
                TargetKind = Str(msg, "targetKind"),
                Value = Str(msg, "value"),
                SelectedText = Str(msg, "selectedText"),
                Masked = msg["masked"]?.GetValue<bool>() ?? false,
                Checked = msg["checked"]?.GetValue<bool>(),
                Url = Str(msg, "url"),
                Ts = msg["ts"]?.GetValue<long>() ?? NowMs(),
            };
            var fpNode = msg["fingerprint"];
            if (fpNode != null)
                evt.Fingerprint = JsonSerializer.Deserialize<ElementFingerprint>(fpNode.ToJsonString(), AutomataJson.Options);
            recorded.Add(evt);
            await PushRecordedPreviewAsync();
        }
        catch (Exception ex)
        {
            await logAsync($"⚠ Recorder event dropped: {ex.Message}");
        }
    }

    /// <summary>Host-side navigation capture: more reliable than page-side unload hooks, and the
    /// fresh document's recorder starts dormant — re-arm it while a recording is live.</summary>
    public async Task OnTargetNavigationCompletedAsync(string url)
    {
        if (!recording) return;
        recorded.Add(new RecorderEvent { Kind = "navigate", Url = url, Ts = NowMs() });
        var core = targetCore();
        if (core != null)
            await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.enable()");
        await PushRecordedPreviewAsync();
    }

    // ---- replay --------------------------------------------------------------------------------

    private Task<bool> RunReplayAsync(string taskId, bool allowRepair = false) =>
        RunEngineAsync(taskId, allowRepair, gap: null);

    /// <summary>Runs every task in a collection, in <see cref="Collection.TaskOrder"/> sequence,
    /// continuing to the remaining tasks even if one fails — tasks in a collection are usually
    /// independent, so one failing shouldn't block the others. Reports a final pass/fail summary.
    /// Cancel (see <c>collectionCancelRequested</c>) stops the whole collection, not just whichever
    /// task happens to be running.</summary>
    private async Task RunCollectionAsync(string collectionId, bool allowRepair)
    {
        var collection = store.GetCollection(collectionId);
        if (collection == null)
        {
            await logAsync($"⚠ Collection '{collectionId}' not found.");
            return;
        }
        if (runActive)
        {
            await logAsync("⚠ A run is already in progress — wait for it to finish or cancel it first.");
            return;
        }
        runActive = true;
        collectionCancelRequested = false;
        await execPanelScript("window.ssPanel.onRunState(true)");

        currentRun = runs.CreateRun(RunTargetKind.Collection, collection.Id, collection.Name);
        var tasks = store.LoadTasks(collectionId);
        // Collection-scope policy: whether one task failing ends the collection. The floor is
        // true (keep going), which is what this loop has always done.
        var policy = EngineSettingsResolver.Resolve(settingsStore.Load(), collection.Settings);
        var passed = 0;
        foreach (var task in tasks)
        {
            if (collectionCancelRequested) break;
            await execPanelScript($"window.ssPanel.onTaskStarted({JsonSerializer.Serialize(new { taskId = task.Id, collectionId }, AutomataJson.Options)})");
            if (await RunEngineAsync(task.Id, allowRepair, gap: null, ownsLifecycle: false))
            {
                passed++;
            }
            else if (!policy.ContinueOnTaskError)
            {
                await logAsync($"⏹ Stopping '{collection.Name}' after '{task.Name}' failed — continue-on-task-error is off for this collection.");
                break;
            }
        }

        var collectionSummary = $"{passed}/{tasks.Count} task(s) passed.";
        await logAsync($"▶ Collection '{collection.Name}': {collectionSummary}");
        runs.CompleteRun(currentRun.RunId, passed == tasks.Count, collectionSummary);
        currentRun = null;
        runActive = false;
        await execPanelScript("window.ssPanel.onRunState(false)");
        await PushRunsAsync();
    }

    /// <summary>Runs the task up to (and pausing before) the step occupying an insert-zone gap —
    /// or, when the gap is the last slot in the whole tree, to completion — then arms recording
    /// so the next physical action(s) become the new step(s) at that gap.</summary>
    private async Task RecordAtGapAsync(JsonNode msg)
    {
        var taskId = Str(msg, "taskId") ?? "";
        var parentStepId = Str(msg, "parentStepId");
        var index = msg["index"]?.GetValue<int>() ?? 0;
        var nextStepId = Str(msg, "nextStepId");
        await RunEngineAsync(taskId, allowRepair: false, gap: new GapTarget(parentStepId, index, nextStepId));
    }

    private async Task<bool> RunEngineAsync(string taskId, bool allowRepair, GapTarget? gap, bool ownsLifecycle = true)
    {
        var surface = targetSurface();
        if (surface == null)
        {
            await logAsync("⚠ Target browser isn't ready yet.");
            return false;
        }
        var task = store.GetTask(taskId);
        if (task == null)
        {
            await logAsync($"⚠ Task '{taskId}' not found.");
            return false;
        }
        if (ownsLifecycle && runActive)
        {
            await logAsync("⚠ A run is already in progress — wait for it to finish or cancel it first.");
            return false;
        }
        if (ownsLifecycle) runActive = true;

        replayCts = new CancellationTokenSource();
        replayControl = new ReplayControl();

        // Resolve the scope chain once per run and hand the engine a per-step lookup, so a
        // timeout, retry policy or self-heal flag set on the collection, the task or one
        // individual step all reach the engine through the same path.
        var globalSettings = settingsStore.Load();
        var collectionSettings = store.GetCollection(task.CollectionId)?.Settings;
        var options = new ReplayOptions
        {
            Control = replayControl,
            AllowLlmRepair = allowRepair,
            PauseBeforeStepId = gap?.PauseBeforeStepId,
            // Parking exists to give a pooled browser lane back during a long wait. This window
            // has exactly one browser pane, and it is not pooled — releasing it would free nothing
            // and would make a run the user is watching disappear from under them. So a long wait
            // here holds the pane and says so; the headless runner is what parks and resumes.
            AllowParking = false,
            ResolveForStep = step =>
            {
                var resolved = EngineSettingsResolver.Resolve(
                    globalSettings, collectionSettings, task.Settings, step.Settings);
                // The sidebar's "allow LLM repair" checkbox is a per-run opt-in that can only
                // turn repair ON. Leaving it unchecked does not disable repair for a scope that
                // deliberately enabled it.
                return allowRepair ? resolved with { AllowLlmRepair = true } : resolved;
            },
        };
        var runLog = new RunLogWriter(task.Name);
        // A collection run already opened a record; a lone task opens its own and closes it below.
        var ownsRun = currentRun == null;
        var run = currentRun ?? runs.CreateRun(RunTargetKind.Task, task.CollectionId, task.Name);
        var outputs = new Dictionary<string, Dictionary<string, string>>();
        var healed = false;
        var success = false;

        if (ownsLifecycle) await execPanelScript("window.ssPanel.onRunState(true)");
        await logAsync($"▶ Run '{task.Name}' — log: {runLog.FilePath}");
        try
        {
            await foreach (var evt in engine.RunAsync(task, options, surface, replayCts.Token))
            {
                var line = FormatStepEvent(evt);
                runLog.WriteLine(line);
                runs.AppendEvent(run.RunId, task.Id, new { kind = evt.GetType().Name, detail = line });
                await logAsync(line);
                switch (evt)
                {
                    case StepEvent.StepStarted s:
                        await PushStepStatusAsync(s.StepId, "running", null);
                        break;
                    case StepEvent.StepCompleted c:
                        if (c.Status == StepStatus.Healed) healed = true;
                        // Where an extracted value finally lands durably, rather than scrolling
                        // out of the log.
                        if (c.ExtractedText != null)
                            outputs[c.StepId] = new Dictionary<string, string> { ["text"] = c.ExtractedText };
                        await PushStepStatusAsync(c.StepId, c.Status.ToString().ToLowerInvariant(), c.Message);
                        break;
                    case StepEvent.StepPaused p:
                        await PushStepStatusAsync(p.StepId, "paused", null);
                        await execPanelScript($"window.ssPanel.onPaused({JsonSerializer.Serialize(p.StepId)})");
                        if (gap is { PauseBeforeStepId: not null } g && p.StepId == g.PauseBeforeStepId)
                            await ArmRecordingForInsertAsync(task.Id, g.ParentStepId, g.Index, runStillSuspended: true);
                        break;
                    case StepEvent.RunCompleted rc:
                        success = rc.Success;
                        if (gap is { PauseBeforeStepId: null } g2 && rc.Success)
                            await ArmRecordingForInsertAsync(task.Id, g2.ParentStepId, g2.Index, runStillSuspended: false);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            runLog.WriteLine("Cancelled.");
            await logAsync("Run cancelled.");
        }
        catch (Exception ex)
        {
            runLog.WriteLine($"Unexpected failure — {ex.Message}");
            await logAsync($"⚠ Unexpected failure — {ex.Message}");
        }

        if (outputs.Count > 0) runs.SaveOutputs(run.RunId, task.Id, outputs);
        if (ownsRun)
        {
            runs.CompleteRun(run.RunId, success, success ? "Passed." : "Failed.");
            await PushRunsAsync();
        }

        if (healed)
        {
            store.SaveTask(task);
            await logAsync("Self-healed fingerprints saved back into the task.");
            await PushTaskAsync(task);
        }

        // Keep runActive/"running" true while a gap-recording session is now armed (mid-tree
        // pause, or the gap was the last slot and the run just completed) — otherwise a second
        // Run/record-at-gap could start concurrently and clobber replayCts/replayControl/
        // pendingGapInsert while the user is still mid-recording. A mid-tree pause keeps this
        // method itself suspended on the loop above until Stop/Continue, so this only actually
        // fires immediately for the end-of-tree/append case (RunCompleted is the terminal event,
        // so the loop exits right after arming) and for the "never armed" case below.
        // StopRecordingAsync/CancelGapRecordingAsync clear both once the session truly ends.
        if (ownsLifecycle && !recording)
        {
            runActive = false;
            await execPanelScript("window.ssPanel.onRunState(false)");
        }

        // A record-at-gap attempt that never reached its pause point (an earlier step failed, or
        // the run was cancelled first) never armed recording — tell the panel anyway so a stale
        // "gap-active" insertion-zone highlight (set optimistically when the attempt started)
        // gets cleared instead of sticking forever.
        if (gap != null && !recording)
            await execPanelScript("window.ssPanel.onRecordingState(false)");

        return success;
    }

    private static string FormatStepEvent(StepEvent evt) => evt switch
    {
        StepEvent.RunStarted r => $"Run started: '{r.TaskName}'",
        StepEvent.StepStarted s => $"→ {s.Label}",
        StepEvent.StepCompleted c => $"{StatusGlyph(c.Status)} {c.StepId}: {c.Message ?? c.Status.ToString()}" +
                                     (c.ExtractedText != null ? $" ⇒ \"{c.ExtractedText}\"" : ""),
        StepEvent.StepPaused p => $"⏸ Paused at '{p.Label}' — press Continue.",
        StepEvent.RunCompleted r => $"{(r.Success ? "✓" : "✗")} {r.Summary}",
        StepEvent.Log l => l.Message,
        _ => evt.ToString() ?? "",
    };

    private static string StatusGlyph(StepStatus status) => status switch
    {
        StepStatus.Passed => "✓",
        StepStatus.Healed => "✓♻",
        StepStatus.Skipped => "▷",
        _ => "✗",
    };

    // ---- import / export -----------------------------------------------------------------------

    private async Task ExportAsync(JsonNode msg)
    {
        var collectionId = Str(msg, "collectionId");
        var taskId = Str(msg, "taskId");
        string? display =
            collectionId != null ? store.GetCollection(collectionId)?.Name :
            taskId != null ? store.GetTask(taskId)?.Name : null;
        if (display == null)
        {
            await logAsync("⚠ Nothing selected to export.");
            return;
        }

        // Recorder JSON rides on the existing Export flow as a second file type rather than a
        // tenth toolbar button: "export as" is already the question being asked.
        var dialog = new SaveFileDialog
        {
            FileName = ArchiveService.SuggestedZipName(display),
            Filter = "Automata export (*.automata.zip)|*.automata.zip|Zip archive (*.zip)|*.zip"
                   + "|Chrome DevTools Recorder (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (taskId == null)
                {
                    await logAsync("⚠ Recorder JSON holds one flow — select a task, not a collection.");
                    return;
                }
                var task = store.GetTask(taskId)!;
                await File.WriteAllTextAsync(dialog.FileName, RecorderFlowIO.Export(task));
                await logAsync($"Exported '{display}' as a Recorder flow to {dialog.FileName}");
                return;
            }

            var zip = collectionId != null
                ? archive.ExportCollection(collectionId, dialog.FileName)
                : archive.ExportTask(taskId!, dialog.FileName);
            await logAsync($"Exported '{display}' to {zip}");
        }
        catch (Exception ex)
        {
            await logAsync($"⚠ Export failed: {ex.Message}");
        }
    }

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Automata export (*.zip)|*.zip|Chrome DevTools Recorder (*.json)|*.json|All files|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await ImportRecorderFlowAsync(dialog.FileName);
                return;
            }

            var result = archive.Import(dialog.FileName);
            foreach (var warning in result.Warnings)
                await logAsync($"⚠ {warning}");
            await logAsync($"Imported {result.Collections.Count} collection(s), {result.Tasks.Count} task(s).");
            await PushStateAsync();
        }
        catch (Exception ex)
        {
            await logAsync($"⚠ Import failed: {ex.Message}");
        }
    }

    // ---- panel pushes --------------------------------------------------------------------------

    /// <summary>Full collections→tasks tree, re-sent after every mutation — keeps the panel JS dumb.</summary>
    /// <summary>
    /// Seeds the generated examples the first time the panel asks for state in this process.
    /// <para>
    /// Once per launch rather than once ever: it restores a page somebody deleted and refreshes an
    /// untouched example that an older build produced, both of which are cheap and both of which
    /// prevent the demo batch quietly rotting. A failure here is logged and swallowed — a
    /// generated example is a convenience, and nothing about it is worth refusing to open the app
    /// over.
    /// </para>
    /// </summary>
    private async Task SeedDemosOnceAsync()
    {
        if (demosSeeded) return;
        demosSeeded = true;
        try
        {
            var report = demos.SeedMissing();
            if (report.Added.Count > 0)
                await logAsync($"Added example task(s): {string.Join(", ", report.Added)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await logAsync($"⚠ Could not write the examples to {demos.RootPath}: {ex.Message}");
        }
    }

    private bool demosSeeded;

    /// <summary>
    /// Arms the target pane to answer the next click as a harvest pick rather than acting on it.
    /// <para>
    /// This is how a harvest gets built without anybody typing a selector: the user clicks one
    /// product tile and the page reports what "all the tiles like this one" resolves to, and how
    /// many there are, so the count is confirmed on screen before it is stored.
    /// </para>
    /// </summary>
    private async Task ArmHarvestPickAsync(string mode, string itemSelector)
    {
        var surface = targetCore();
        if (surface == null)
        {
            await logAsync("⚠ The browser pane isn't ready yet — wait for it to load, then try again.");
            return;
        }

        var arg = JsonSerializer.Serialize(itemSelector, AutomataJson.Options);
        var wanted = mode == "field" ? "field" : "row";
        await TryEvalAsync(surface,
            $"window.__automataRecorder && window.__automataRecorder.pick(\"{wanted}\", {arg})");
        await logAsync(wanted == "row"
            ? "Click one item in the page — the whole list like it becomes the harvest."
            : "Click the value inside that item you want as a column.");
    }

    /// <summary>Evaluates in the target pane, swallowing the failure a closed pane throws.</summary>
    private static async Task TryEvalAsync(CoreWebView2 surface, string script)
    {
        try { await surface.ExecuteScriptAsync(script); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { /* pane gone */ }
    }

    private Task PushDemoSurveyAsync()
    {
        var json = JsonSerializer.Serialize(new
        {
            root = demos.RootPath,
            items = demos.Survey().Select(s => new
            {
                key = s.Key,
                name = s.Name,
                state = s.State.ToString().ToLowerInvariant(),
                taskId = s.TaskId,
            }),
        }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onDemoSurvey({json})");
    }

    private static string SummariseDemoRegeneration(DemoSeedReport report)
    {
        var parts = new List<string>();
        void Add(string label, IReadOnlyList<string> names)
        {
            if (names.Count > 0) parts.Add($"{label} {string.Join(", ", names)}");
        }
        Add("added", report.Added);
        Add("refreshed", report.Refreshed);
        Add("restored", report.Restored);
        Add("left alone", report.Kept);

        return parts.Count == 0
            ? "Examples are already up to date."
            : $"Examples: {string.Join("; ", parts)}.";
    }

    public Task PushStateAsync()
    {
        var tree = store.LoadCollections().Select(c => new
        {
            id = c.Id,
            name = c.Name,
            description = c.Description,
            // The panel needs the collection's own overrides to show what a task or step
            // inherits, and from which scope. Tasks (and their steps) already carry theirs.
            settings = c.Settings,
            tasks = store.LoadTasks(c.Id),
        });
        // Named so the first-run tutorial can tell "this person has built nothing yet" from
        // "this person has nothing but the examples we generated for them". Without it, seeding
        // the examples would silently suppress the tutorial — and the tutorial surviving every
        // change is the one invariant this project does not trade away.
        var demoCollectionId = store.LoadCollections()
            .FirstOrDefault(c => c.Name == DemoTasks.CollectionName)?.Id;
        var json = JsonSerializer.Serialize(
            new { collections = tree, demoCollectionId }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onState({json})");
    }

    /// <summary>
    /// One task, after a change that touched only that task.
    /// <para>
    /// <see cref="PushStateAsync"/> serialises every collection and every task in the workspace,
    /// which is the right answer when the shape of the tree changed and a needlessly expensive one
    /// when a single step was edited — and a step edit is the thing that happens most. The panel
    /// splices this into the tree it already has.
    /// </para>
    /// <para>
    /// The task is sent AFTER <see cref="CollectionStore.SaveTask"/>, which mutates it: the
    /// collection an unassigned task landed in, the timestamp, and the name it was given if
    /// another file already had that one. Sending the object the panel supplied would show the
    /// user a name the store did not accept.
    /// </para>
    /// <para>
    /// A task that arrives for a collection the panel does not know about is the one case a delta
    /// cannot apply, and the panel answers it by asking for the whole state — so the protocol can
    /// always fall back to the truth rather than guess.
    /// </para>
    /// </summary>
    public Task PushTaskAsync(TaskDefinition task)
    {
        var json = JsonSerializer.Serialize(
            new { collectionId = task.CollectionId, task }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onTaskChanged({json})");
    }

    private Task PushRecordedPreviewAsync()
    {
        var steps = RecorderSessionBuilder.Build(recorded);
        var json = JsonSerializer.Serialize(steps, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onRecordedSteps({json})");
    }

    /// <summary>Delivers the step(s) captured by a record-at-gap session to the panel, which
    /// splices them into the tree via the same insertion path as a manually created step.</summary>
    private Task PushGapRecordedAsync(string taskId, string? parentStepId, int index, List<Step> steps)
    {
        var json = JsonSerializer.Serialize(new { taskId, parentStepId, index, steps }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onGapRecorded({json})");
    }

    /// <summary>
    /// Deserializes a scope's engine overrides, collapsing one that overrides nothing to null so
    /// an untouched entity never gains an empty settings node on disk.
    /// </summary>
    private static EngineSettingsOverride? ParseOverride(JsonNode? node)
    {
        if (node == null) return null;
        var parsed = JsonSerializer.Deserialize<EngineSettingsOverride>(node.ToJsonString(), AutomataJson.Options);
        return parsed is null || parsed.IsEmpty ? null : parsed;
    }

    private static IEnumerable<(string Field, Action<AutomataSettings, string> Apply)> KeyFields() =>
    [
        ("claudeKey", (s, v) => s.AnthropicApiKey = v),
        ("openaiKey", (s, v) => s.OpenAiApiKey = v),
        ("geminiKey", (s, v) => s.GeminiApiKey = v),
        ("kimiKey", (s, v) => s.KimiApiKey = v),
    ];

    /// <summary>Settings for the panel — keys themselves never cross the bridge, only hints.</summary>
    public Task PushSettingsAsync()
    {
        var settings = settingsStore.Load();
        static object Hint(string? key, string fallbackLabel) => new
        {
            set = !string.IsNullOrEmpty(key),
            // A key too short to safely show a masked suffix still IS a BYO override — showing
            // the "Vault/default" fallback label for it would contradict `set: true` and read as
            // if no override were active.
            hint = string.IsNullOrEmpty(key) ? fallbackLabel
                : key.Length >= 4 ? "BYO …" + key[^4..]
                : "BYO key set",
        };
        var json = JsonSerializer.Serialize(new
        {
            provider = settings.Provider,
            borderRadius = settings.BorderRadius,
            // Coerced on the way out as well as on the way in: the file on disk is hand-editable,
            // and a name the panel does not know would leave it with no palette at all.
            theme = AutomataSettings.Themes.Coerce(settings.Theme),
            // The outermost link of the settings chain, plus the floor beneath it. The floor is
            // sent rather than mirrored in JS so there is exactly one definition of it.
            engineDefaults = settings.EngineDefaults,
            engineFloor = EngineSettingsResolver.Floor(),
            keys = new
            {
                claude = Hint(settings.AnthropicApiKey, "OAuth/Vault default"),
                openai = Hint(settings.OpenAiApiKey, "Vault 'openai'"),
                gemini = Hint(settings.GeminiApiKey, "Vault 'gemini'"),
                kimi = Hint(settings.KimiApiKey, "Vault 'kimi'"),
            },
        }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onSettings({json})");
    }

    /// <summary>
    /// Brings in a Chrome DevTools Recorder flow. Anything outside the overlapping subset is
    /// logged rather than dropped quietly, so the user knows what did not survive the crossing.
    /// </summary>
    private async Task ImportRecorderFlowAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        var result = RecorderFlowIO.Import(json, Path.GetFileNameWithoutExtension(path));
        foreach (var warning in result.Warnings) await logAsync($"⚠ {warning}");

        if (result.Task.Steps.Count == 0)
        {
            await logAsync("⚠ Nothing importable in that recording.");
            return;
        }

        result.Task.CollectionId = store.EnsureCollectionNamed("Imported").Id;
        store.SaveTask(result.Task);
        await logAsync($"Imported '{result.Task.Name}' with {result.Task.Steps.Count} step(s) from a Recorder flow.");
        await PushStateAsync();
    }

    // ---- authoring -----------------------------------------------------------------------------

    /// <summary>
    /// Turns a description into a feature file and compiles it, then shows BOTH to the user.
    /// Nothing is written to the store until they accept it — the whole point of having a readable
    /// intermediate artifact is that it can be reviewed first.
    /// </summary>
    private async Task DraftFlowAsync(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            await logAsync("⚠ Describe what the task should do first.");
            return;
        }

        await logAsync("✎ Drafting steps…");
        try
        {
            var context = new FlowAuthoringContext
            {
                DatasetNames = datasets.List(),
                TaskNames = store.LoadCollections().SelectMany(c => store.LoadTasks(c.Id)).Select(t => t.Name).ToList(),
                CurrentUrl = targetCore()?.Source,
            };
            pendingDraft = await authoring.DraftAsync(description, context);
            await logAsync(pendingDraft.Result.HasErrors
                ? $"⚠ {pendingDraft.Provider} wrote a feature that does not compile after {pendingDraft.Attempts} attempt(s)."
                : $"✓ {pendingDraft.Provider} drafted {pendingDraft.Result.Tasks.Count} task(s) in {pendingDraft.Attempts} attempt(s).");
            await PushFlowDraftAsync();
        }
        catch (Exception ex)
        {
            pendingDraft = null;
            await logAsync($"⚠ Drafting failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compiles feature text the user edited by hand. Deliberately does NOT involve the model: a
    /// hand edit is held to exactly the same standard as a drafted one, and re-rolling it through
    /// an LLM would quietly discard what they wrote.
    /// </summary>
    private async Task CompileFlowAsync(string featureText)
    {
        var result = GherkinFlowCompiler.Compile(featureText);
        pendingDraft = new FlowDraft(featureText, result, 0, "your edit");
        await logAsync(result.HasErrors
            ? "⚠ The edited feature does not compile."
            : $"✓ The edited feature compiles to {result.Tasks.Count} task(s).");
        await PushFlowDraftAsync();
    }

    /// <summary>Saves the reviewed draft: the collection, its tasks, and any dataset its Examples
    /// tables produced.</summary>
    private async Task InsertDraftAsync()
    {
        if (pendingDraft?.Result.Collection == null)
        {
            await logAsync("⚠ Nothing to insert — draft something first.");
            return;
        }

        var result = pendingDraft.Result;
        store.SaveCollection(result.Collection);
        foreach (var task in result.Tasks)
        {
            task.CollectionId = result.Collection.Id;
            store.SaveTask(task);
        }
        foreach (var dataset in result.Datasets)
            datasets.Write(dataset.Name, dataset.Rows, append: false);

        await logAsync($"✓ Added '{result.Collection.Name}' with {result.Tasks.Count} task(s)" +
            (result.Datasets.Count > 0 ? $" and {result.Datasets.Count} dataset(s)." : "."));
        pendingDraft = null;
        await PushStateAsync();
        await PushDatasetsAsync();
    }

    /// <summary>Renders a saved task back to Gherkin for reading.</summary>
    private async Task PushFeatureAsync(string taskId)
    {
        var task = store.GetTask(taskId);
        var collection = task == null ? null : store.GetCollection(task.CollectionId);
        if (task == null || collection == null)
        {
            await logAsync($"⚠ Task '{taskId}' not found.");
            return;
        }

        var written = GherkinWriter.Write(collection, [task]);
        var json = JsonSerializer.Serialize(new
        {
            taskName = task.Name,
            featureText = written.Text,
            isLossy = written.IsLossy,
            reasons = written.Reasons,
        }, AutomataJson.Options);
        await execPanelScript($"window.ssPanel.onFeatureView({json})");
    }

    private Task PushFlowDraftAsync()
    {
        var draft = pendingDraft;
        if (draft == null) return Task.CompletedTask;

        var json = JsonSerializer.Serialize(new
        {
            featureText = draft.FeatureText,
            provider = draft.Provider,
            attempts = draft.Attempts,
            canInsert = !draft.Result.HasErrors,
            collectionName = draft.Result.Collection?.Name,
            diagnostics = draft.Result.Diagnostics.Select(d => new
            {
                severity = d.Severity.ToString().ToLowerInvariant(),
                line = d.Line,
                message = d.Message,
            }),
            tasks = draft.Result.Tasks.Select(t => new
            {
                name = t.Name,
                steps = Outline(t.Steps, 0),
            }),
            datasets = draft.Result.Datasets.Select(d => new { name = d.Name, rows = d.Rows.Count }),
        }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onFlowDraft({json})");
    }

    /// <summary>Flattens a compiled step tree into indented labels, so the preview shows the shape
    /// the feature actually produced — nesting included.</summary>
    private static List<string> Outline(IReadOnlyList<Step> steps, int depth)
    {
        var lines = new List<string>();
        foreach (var step in steps)
        {
            lines.Add(new string(' ', depth * 2) + (string.IsNullOrWhiteSpace(step.Label) ? step.Action.ToString() : step.Label));
            lines.AddRange(Outline(step.Children, depth + 1));
        }
        return lines;
    }

    // ---- schedule ------------------------------------------------------------------------------

    /// <summary>
    /// Adds or replaces a schedule entry the sidebar authored.
    /// <para>
    /// Everything is refused up front rather than saved and left to never fire — a schedule that
    /// quietly does nothing is the worst failure mode this feature has. The reason travels back to
    /// the panel with the pushed schedule, so the editor can show it beside the field that caused
    /// it instead of only writing it to the log.
    /// </para>
    /// </summary>
    private async Task SaveScheduleEntryAsync(JsonNode msg)
    {
        ScheduleEntry? entry = null;
        try
        {
            if (msg["entry"] is { } node)
                entry = JsonSerializer.Deserialize<ScheduleEntry>(node.ToJsonString(), AutomataJson.Options);
        }
        catch (JsonException ex)
        {
            await logAsync($"⚠ Schedule entry could not be read: {ex.Message}");
            await PushScheduleAsync("That schedule could not be read.");
            return;
        }

        if (entry == null)
        {
            await PushScheduleAsync("That schedule could not be read.");
            return;
        }

        // A blank id would collapse every new entry onto one another in the store, so it is minted
        // here rather than trusted — the model's own default only applies when the field is absent
        // altogether, and an empty string is not absent.
        if (string.IsNullOrWhiteSpace(entry.Id)) entry.Id = Guid.NewGuid().ToString("n");

        var existing = schedule.Get(entry.Id);
        if (ValidateScheduleEntry(entry, existing) is { } problem)
        {
            await logAsync($"⚠ {problem}");
            await PushScheduleAsync(problem);
            return;
        }

        // Bookkeeping belongs to the scheduler, not to whoever edited the entry — the panel may
        // send back whatever it was last shown, and it must not be able to rewrite run history.
        entry.LastRunUtc = existing?.LastRunUtc;
        entry.LastOutcome = existing?.LastOutcome;

        // The written-down due time is only recomputed when the triggers actually changed. Keeping
        // it otherwise is what lets a firing that was missed while nothing was running survive an
        // unrelated edit (a rename, say) instead of being quietly pushed forward.
        var triggersChanged = existing == null
            || JsonSerializer.Serialize(existing.Triggers, AutomataJson.Options)
               != JsonSerializer.Serialize(entry.Triggers, AutomataJson.Options);
        entry.NextDueUtc = triggersChanged ? null : existing!.NextDueUtc;
        entry.NextDueUtc ??= TriggerEvaluator.Evaluate(entry, clock).NextUtc;

        schedule.Upsert(entry);
        var verdict = TriggerEvaluator.Evaluate(entry, clock);
        await logAsync($"Scheduled '{entry.Name}' — {verdict.Reason}.");
        await PushScheduleAsync();
    }

    /// <summary>
    /// How many triggers one entry may carry. Matches the sidebar's own cap, and exists so a
    /// hand-edited schedule.json cannot be saved back as something unreadable.
    /// </summary>
    private const int MaxTriggersPerEntry = 8;

    /// <summary>The reason an entry cannot be saved, or null when it is sound.</summary>
    private string? ValidateScheduleEntry(ScheduleEntry entry, ScheduleEntry? existing)
    {
        if (string.IsNullOrWhiteSpace(entry.Name)) return "A schedule needs a name.";
        if (ScheduleTargetName(entry) == null)
            return entry.Target == ScheduleTargetKind.Task
                ? "Pick a task for this schedule to run."
                : "Pick a collection for this schedule to run.";
        if (entry.Triggers.Count == 0) return "A schedule needs at least one trigger.";
        // Several triggers are the point — "every weekday at 09:00 or once the ingest finishes" —
        // but a bound keeps a hand-edited or scripted entry from turning into something nobody can
        // read, and every one of them is evaluated on every tick.
        if (entry.Triggers.Count > MaxTriggersPerEntry)
            return $"A schedule can have at most {MaxTriggersPerEntry} triggers; past that it is " +
                   "easier to read as two schedules.";

        var entries = schedule.Load();
        foreach (var trigger in entry.Triggers)
        {
            switch (trigger.Kind)
            {
                case TriggerKind.Cron:
                    if (!CronSchedule.TryParse(trigger.CronExpression, out _, out var cronError))
                        return $"That cron expression won't work: {cronError}.";
                    if (TriggerEvaluator.Next(trigger, clock.UtcNow) == null)
                        return "That cron expression never matches a real date — nothing would ever run.";
                    break;

                case TriggerKind.Interval:
                    if (trigger.IntervalSeconds is not > 0)
                        return "An interval needs to be at least one minute.";
                    break;

                case TriggerKind.OneShot:
                    if (trigger.FireAtUtc == null) return "Pick the date and time to run once at.";
                    if (trigger.FireAtUtc <= clock.UtcNow)
                        return "That one-off time has already passed — pick a later one.";
                    break;

                case TriggerKind.AfterEntry:
                    if (string.IsNullOrWhiteSpace(trigger.AfterEntryId))
                        return "Pick the schedule this one should follow.";
                    if (trigger.AfterEntryId == entry.Id)
                        return "A schedule cannot wait for itself to finish.";
                    if (entries.All(e => e.Id != trigger.AfterEntryId))
                        return "The schedule this one follows no longer exists.";
                    break;
            }
        }

        // A chain is allowed — "after the ingest, reconcile" is the point — but a cycle is not,
        // because every entry in one would sit waiting for another entry in the same loop and none
        // of them would ever start. Reached by walking forward from the upstream entry: if this
        // entry is already somewhere downstream of it, closing the link would form the loop.
        var upstreamIds = entry.Triggers
            .Where(t => t.Kind == TriggerKind.AfterEntry && t.AfterEntryId != null)
            .Select(t => t.AfterEntryId!);
        var saved = existing == null ? entries : entries.Where(e => e.Id != entry.Id).ToList();
        foreach (var upstreamId in upstreamIds)
        {
            var reachable = TriggerEvaluator.Chain(saved, upstreamId, succeeded: true);
            if (reachable.Any(e => e.Id == entry.Id))
                return "That would make a loop — the schedule it follows already waits for this one.";
        }

        return null;
    }

    private string? ScheduleTargetName(ScheduleEntry entry) =>
        entry.Target == ScheduleTargetKind.Task
            ? store.GetTask(entry.TargetId)?.Name
            : store.GetCollection(entry.TargetId)?.Name;

    /// <summary>
    /// The schedule, as the sidebar shows it.
    /// <para>
    /// Every derived value — when an entry is next due, why, and what its success sets off — is
    /// computed here by the same <see cref="TriggerEvaluator"/> the runner's <c>tick</c> obeys,
    /// rather than reimplemented in JavaScript. A preview that could disagree with the run would
    /// be worse than no preview.
    /// </para>
    /// </summary>
    public Task PushScheduleAsync(string? error = null)
    {
        var entries = schedule.Load();
        var listed = entries.Select(e =>
        {
            var verdict = TriggerEvaluator.Evaluate(e, clock);
            return new
            {
                id = e.Id,
                name = e.Name,
                enabled = e.Enabled,
                target = e.Target,
                targetId = e.TargetId,
                // Null when the collection or task has since been deleted — the row says so
                // rather than showing a schedule that looks fine and cannot run.
                targetName = ScheduleTargetName(e),
                triggers = e.Triggers,
                nextDueUtc = verdict.NextUtc,
                due = verdict.Due,
                reason = verdict.Reason,
                lastRunUtc = e.LastRunUtc,
                lastOutcome = e.LastOutcome,
                chain = TriggerEvaluator.Chain(entries, e.Id, succeeded: true).Select(d => d.Id),
            };
        });

        var json = JsonSerializer.Serialize(new
        {
            entries = listed,
            error,
            // Offered as a picker rather than typed: an unrecognised zone id would silently fall
            // back to this machine's zone, which looks like the schedule working.
            timeZones = TimeZoneInfo.GetSystemTimeZones()
                .Select(z => new { id = z.Id, label = z.DisplayName }),
            localTimeZoneId = TimeZoneInfo.Local.Id,
        }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onSchedule({json})");
    }

    /// <summary>
    /// The browser lanes running right now, in every Automata process — which in practice means
    /// <c>automata-runner</c>'s.
    /// <para>
    /// This window has one browser pane and no pool, so it has no lanes of its own worth showing;
    /// what it can do is watch the headless runner's. Deliberately read fresh on every poll rather
    /// than cached: the whole value of the strip is that it is current.
    /// </para>
    /// </summary>
    public Task PushLanesAsync()
    {
        var processes = liveLanes.List().Select(p => new
        {
            processId = p.ProcessId,
            processName = p.ProcessName,
            targetName = p.TargetName,
            runId = p.RunId,
            maxConcurrency = p.MaxConcurrency,
            updatedUtc = p.UpdatedUtc,
            lanes = p.Lanes.Select(l => new
            {
                laneId = l.LaneId,
                profileKey = l.ProfileKey,
                busy = l.Busy,
                taskName = l.TaskName,
                stepLabel = l.CurrentStepLabel,
                startedUtc = l.StartedUtc,
            }),
        });
        var json = JsonSerializer.Serialize(new { processes }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onLanes({json})");
    }

    /// <summary>
    /// Recent runs, newest first. This is how the sidebar shows runs it did not start — including
    /// ones the headless runner produced while the window was closed.
    /// </summary>
    public Task PushRunsAsync()
    {
        // A run that checkpointed on a long wait has an open manifest, which on its own is
        // indistinguishable from one still executing. The parked record is what tells them apart,
        // so it is joined in here rather than leaving the tab to show an hours-old run as
        // "running" with no explanation.
        var waiting = parkedRuns.List().ToDictionary(p => p.RunId, StringComparer.Ordinal);
        var recent = runs.ListRuns(limit: 25).Select(r => new
        {
            id = r.RunId,
            target = r.Target.ToString().ToLowerInvariant(),
            name = r.TargetName,
            trigger = r.Trigger,
            startedUtc = r.StartedUtc,
            endedUtc = r.EndedUtc,
            // Null while in flight, which the panel renders as "running" rather than as a result.
            success = r.Success,
            summary = r.Summary,
            parked = waiting.TryGetValue(r.RunId, out var park)
                ? new
                {
                    resumeAtUtc = park.ResumeAtUtc,
                    reason = park.Checkpoint.Reason,
                    stepLabel = park.Checkpoint.StepLabel,
                    taskName = park.TaskName,
                    due = park.ResumeAtUtc <= clock.UtcNow,
                }
                : null,
        });
        var json = JsonSerializer.Serialize(new { root = runs.RootPath, runs = recent }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onRuns({json})");
    }

    /// <summary>
    /// The datasets a task can fan out over or write into. Columns are sampled from each file so
    /// the binding picker can offer real column names instead of asking anyone to type one.
    /// </summary>
    public Task PushDatasetsAsync()
    {
        var listed = datasets.List().Select(name => new
        {
            name,
            columns = datasets.Columns(name),
            rows = datasets.Read(name).Count,
        });
        var json = JsonSerializer.Serialize(
            new { root = datasets.RootPath, datasets = listed }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onDatasets({json})");
    }

    private Task PushStepStatusAsync(string stepId, string status, string? message)
    {
        var json = JsonSerializer.Serialize(new { stepId, status, message }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onStepEvent({json})");
    }

    private static string? Str(JsonNode msg, string key) => msg[key]?.GetValue<string>();
}

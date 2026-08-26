using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Automata.Core.Automation;
using Automata.Core.Automation.Logging;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Recording;
using Automata.Core.Automation.Replay;
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
    private readonly ReplayEngine engine;
    private readonly AutomataSettingsStore settingsStore;
    private readonly Func<IBrowserSurface?> targetSurface;
    private readonly Func<CoreWebView2?> targetCore;
    private readonly Func<string, Task> execPanelScript;
    private readonly Func<string, Task> logAsync;

    private bool recording;
    private readonly List<RecorderEvent> recorded = [];
    private CancellationTokenSource? replayCts;
    private ReplayControl? replayControl;

    public AutomationController(
        CollectionStore store,
        ArchiveService archive,
        ReplayEngine engine,
        AutomataSettingsStore settingsStore,
        Func<IBrowserSurface?> targetSurface,
        Func<CoreWebView2?> targetCore,
        Func<string, Task> execPanelScript,
        Func<string, Task> logAsync)
    {
        this.store = store;
        this.archive = archive;
        this.engine = engine;
        this.settingsStore = settingsStore;
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
                await PushStateAsync();
                return true;

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
                if (taskNode != null)
                {
                    var task = JsonSerializer.Deserialize<TaskDefinition>(taskNode.ToJsonString(), AutomataJson.Options);
                    if (task != null) store.SaveTask(task);
                }
                await PushStateAsync();
                return true;
            }

            case "renameTask":
            {
                var task = store.GetTask(Str(msg, "id") ?? "");
                if (task != null)
                {
                    task.Name = Str(msg, "name") ?? task.Name;
                    store.SaveTask(task);
                }
                await PushStateAsync();
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

                settingsStore.Save(settings);
                await PushSettingsAsync();
                return true;
            }

            case "continueRun":
                replayControl?.Continue();
                return true;

            case "cancelRun":
                replayCts?.Cancel();
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
        recorded.Clear();
        recording = true;
        // Seed with where the user is starting from, so the replay begins on the same page.
        if (!string.IsNullOrEmpty(core.Source) && core.Source != "about:blank")
            recorded.Add(new RecorderEvent { Kind = "navigate", Url = core.Source, Ts = NowMs() });

        await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.enable()");
        await execPanelScript("window.ssPanel.onRecordingState(true)");
        await PushRecordedPreviewAsync();
        await logAsync("● Recording — perform the actions to capture, then press Stop.");
    }

    private async Task StopRecordingAsync(JsonNode msg)
    {
        recording = false;
        var core = targetCore();
        if (core != null)
            await core.ExecuteScriptAsync("window.__automataRecorder && window.__automataRecorder.disable()");

        var steps = RecorderSessionBuilder.Build(recorded);
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

        recorded.Clear();
        await execPanelScript("window.ssPanel.onRecordingState(false)");
        await PushRecordedPreviewAsync();
        await PushStateAsync();
    }

    /// <summary>Raw recorder message from the target pane (already filtered by source tag).</summary>
    public async Task HandleRecorderMessageAsync(JsonNode msg)
    {
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

    private async Task RunReplayAsync(string taskId, bool allowRepair = false)
    {
        var surface = targetSurface();
        if (surface == null)
        {
            await logAsync("⚠ Target browser isn't ready yet.");
            return;
        }
        var task = store.GetTask(taskId);
        if (task == null)
        {
            await logAsync($"⚠ Task '{taskId}' not found.");
            return;
        }

        replayCts = new CancellationTokenSource();
        replayControl = new ReplayControl();
        var options = new ReplayOptions { Control = replayControl, AllowLlmRepair = allowRepair };
        var runLog = new RunLogWriter(task.Name);
        var healed = false;

        await execPanelScript("window.ssPanel.onRunState(true)");
        await logAsync($"▶ Run '{task.Name}' — log: {runLog.FilePath}");
        try
        {
            await foreach (var evt in engine.RunAsync(task, options, surface, replayCts.Token))
            {
                var line = FormatStepEvent(evt);
                runLog.WriteLine(line);
                await logAsync(line);
                switch (evt)
                {
                    case StepEvent.StepStarted s:
                        await PushStepStatusAsync(s.StepId, "running", null);
                        break;
                    case StepEvent.StepCompleted c:
                        if (c.Status == StepStatus.Healed) healed = true;
                        await PushStepStatusAsync(c.StepId, c.Status.ToString().ToLowerInvariant(), c.Message);
                        break;
                    case StepEvent.StepPaused p:
                        await PushStepStatusAsync(p.StepId, "paused", null);
                        await execPanelScript($"window.ssPanel.onPaused({JsonSerializer.Serialize(p.StepId)})");
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

        if (healed)
        {
            store.SaveTask(task);
            await logAsync("Self-healed fingerprints saved back into the task.");
            await PushStateAsync();
        }
        await execPanelScript("window.ssPanel.onRunState(false)");
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

        var dialog = new SaveFileDialog
        {
            FileName = ArchiveService.SuggestedZipName(display),
            Filter = "Automata export (*.automata.zip)|*.automata.zip|Zip archive (*.zip)|*.zip",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
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
        var dialog = new OpenFileDialog { Filter = "Automata export|*.zip|All files|*.*" };
        if (dialog.ShowDialog() != true) return;

        try
        {
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
    public Task PushStateAsync()
    {
        var tree = store.LoadCollections().Select(c => new
        {
            id = c.Id,
            name = c.Name,
            description = c.Description,
            tasks = store.LoadTasks(c.Id),
        });
        var json = JsonSerializer.Serialize(new { collections = tree }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onState({json})");
    }

    private Task PushRecordedPreviewAsync()
    {
        var steps = RecorderSessionBuilder.Build(recorded);
        var json = JsonSerializer.Serialize(steps, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onRecordedSteps({json})");
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
            hint = key is { Length: >= 4 } k ? "BYO …" + k[^4..] : fallbackLabel,
        };
        var json = JsonSerializer.Serialize(new
        {
            provider = settings.Provider,
            borderRadius = settings.BorderRadius,
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

    private Task PushStepStatusAsync(string stepId, string status, string? message)
    {
        var json = JsonSerializer.Serialize(new { stepId, status, message }, AutomataJson.Options);
        return execPanelScript($"window.ssPanel.onStepEvent({json})");
    }

    private static string? Str(JsonNode msg, string key) => msg[key]?.GetValue<string>();
}

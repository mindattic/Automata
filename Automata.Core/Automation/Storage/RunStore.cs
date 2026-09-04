using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>What a run was launched against.</summary>
public enum RunTargetKind { Task, Collection }

/// <summary>The durable record of one run.</summary>
public sealed class RunManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string RunId { get; set; } = "";
    public RunTargetKind Target { get; set; }
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";

    /// <summary>"manual" | "schedule" | "dependency" — how the run was started.</summary>
    public string Trigger { get; set; } = "manual";

    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }

    /// <summary>Null while the run is still in flight.</summary>
    public bool? Success { get; set; }

    public string? Summary { get; set; }
}

/// <summary>
/// Durable run artifacts: the manifest, a per-task event log, the values steps published, and any
/// datasets a run wrote.
/// <para>
/// This is where <c>ExtractText</c>'s captured value finally has somewhere to go — until now it
/// reached the log and was dropped. It is also what lets the sidebar show runs it did not start,
/// including ones that finished while the window was closed.
/// </para>
/// <code>
/// Documents\Automata\Runs\&lt;yyyyMMdd-HHmmss&gt;-&lt;slug&gt;\
///     manifest.json
///     tasks\&lt;taskId&gt;\events.jsonl     one event per line, append-only
///     tasks\&lt;taskId&gt;\outputs.json     { stepId: { outputName: value } }
///     datasets\&lt;name&gt;.csv|.json
/// </code>
/// The timestamp-first directory name means listing runs newest-first is an ordinary name sort,
/// and it matches the convention <see cref="Logging.RunLogWriter"/> already uses.
/// </summary>
public sealed class RunStore
{
    public const string ManifestFileName = "manifest.json";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Runs");

    public string RootPath { get; }

    public RunStore(string? rootPath = null) => RootPath = rootPath ?? DefaultRoot;

    /// <summary>
    /// Creates a run directory and its manifest. Nothing is written until a run actually starts,
    /// so a fresh install has no Runs tree at all.
    /// </summary>
    public RunManifest CreateRun(RunTargetKind target, string targetId, string targetName, string trigger = "manual")
    {
        var manifest = new RunManifest
        {
            RunId = StoreUtil.NewId(),
            Target = target,
            TargetId = targetId,
            TargetName = targetName,
            Trigger = trigger,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        var dir = Path.Combine(RootPath, DirectoryNameFor(manifest));
        Directory.CreateDirectory(dir);
        WriteManifest(dir, manifest);
        return manifest;
    }

    /// <summary>Absolute path of a run's directory, or null if it no longer exists.</summary>
    public string? DirectoryFor(string runId) => FindDirectory(runId);

    public string DatasetPath(string runId, string datasetName)
    {
        var dir = FindDirectory(runId) ?? throw new InvalidOperationException($"Unknown run '{runId}'.");
        var datasets = Path.Combine(dir, "datasets");
        Directory.CreateDirectory(datasets);
        return Path.Combine(datasets, StoreUtil.SafeFileName(datasetName));
    }

    /// <summary>Appends one event as a JSON line. Unbuffered, like the run log: a crashed run
    /// still leaves everything it had already reported.</summary>
    public void AppendEvent(string runId, string taskId, object evt)
    {
        var dir = TaskDirectory(runId, taskId);
        if (dir == null) return;
        File.AppendAllText(
            Path.Combine(dir, "events.jsonl"),
            JsonSerializer.Serialize(evt, AutomataJson.Options).ReplaceLineEndings(" ") + "\n");
    }

    /// <summary>Persists the values one task's steps published, keyed step id → output name.</summary>
    public void SaveOutputs(string runId, string taskId, IReadOnlyDictionary<string, Dictionary<string, string>> outputs)
    {
        var dir = TaskDirectory(runId, taskId);
        if (dir == null) return;
        File.WriteAllText(Path.Combine(dir, "outputs.json"),
            JsonSerializer.Serialize(outputs, AutomataJson.Options));
    }

    public IReadOnlyDictionary<string, Dictionary<string, string>> LoadOutputs(string runId, string taskId)
    {
        var dir = TaskDirectory(runId, taskId, create: false);
        var file = dir == null ? null : Path.Combine(dir, "outputs.json");
        if (file == null || !File.Exists(file)) return new Dictionary<string, Dictionary<string, string>>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllText(file), AutomataJson.Options) ?? [];
        }
        catch (JsonException) { return new Dictionary<string, Dictionary<string, string>>(); }
    }

    public void CompleteRun(string runId, bool success, string summary)
    {
        var dir = FindDirectory(runId);
        if (dir == null) return;
        var manifest = ReadManifest(dir);
        if (manifest == null) return;
        manifest.Success = success;
        manifest.Summary = summary;
        manifest.EndedUtc = DateTimeOffset.UtcNow;
        WriteManifest(dir, manifest);
    }

    /// <summary>
    /// Most recent runs first. Directory names lead with a sortable timestamp, so this is a name
    /// sort rather than a stat of every manifest.
    /// </summary>
    public IReadOnlyList<RunManifest> ListRuns(int limit = 50)
    {
        if (!Directory.Exists(RootPath)) return [];
        return Directory.EnumerateDirectories(RootPath)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(ReadManifest)
            .Where(m => m != null)
            .Take(limit)
            .ToList()!;
    }

    public RunManifest? GetRun(string runId)
    {
        var dir = FindDirectory(runId);
        return dir == null ? null : ReadManifest(dir);
    }

    // ---- internals ---------------------------------------------------------------------------

    private static string DirectoryNameFor(RunManifest manifest) =>
        $"{manifest.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{StoreUtil.Slug(manifest.TargetName)}-{manifest.RunId[..8]}";

    private string? FindDirectory(string runId)
    {
        if (!Directory.Exists(RootPath) || string.IsNullOrEmpty(runId)) return null;
        // The id's prefix is in the directory name, so this is a name match rather than a scan of
        // every manifest on disk.
        var suffix = "-" + (runId.Length >= 8 ? runId[..8] : runId);
        return Directory.EnumerateDirectories(RootPath)
            .FirstOrDefault(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.Ordinal));
    }

    private string? TaskDirectory(string runId, string taskId, bool create = true)
    {
        var runDir = FindDirectory(runId);
        if (runDir == null) return null;
        var dir = Path.Combine(runDir, "tasks", StoreUtil.SafeFileName(taskId));
        if (create) Directory.CreateDirectory(dir);
        else if (!Directory.Exists(dir)) return null;
        return dir;
    }

    private static void WriteManifest(string dir, RunManifest manifest) =>
        File.WriteAllText(Path.Combine(dir, ManifestFileName),
            JsonSerializer.Serialize(manifest, AutomataJson.Options));

    private static RunManifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, ManifestFileName);
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(file), AutomataJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

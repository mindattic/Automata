using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>Envelope identifying a zip as an Automata export.</summary>
public sealed class ExportManifest
{
    public string Format { get; set; } = ArchiveService.FormatName;
    public int SchemaVersion { get; set; } = SchemaMigration.CurrentExportVersion;

    /// <summary>"collection" or "task".</summary>
    public string Type { get; set; } = "";

    public DateTimeOffset ExportedUtc { get; set; }
    public string AppVersion { get; set; } = "";
}

/// <summary>
/// A <c>.automata.json</c> export: one document holding a task, a collection with its tasks, or
/// the whole workspace. The envelope fields match <see cref="ExportManifest"/>; which of the rest
/// are present depends on <see cref="Type"/>. Tasks and collections are in exactly the shape the
/// old per-task files and the zip entries use.
/// </summary>
public sealed class JsonExport
{
    public string Format { get; set; } = ArchiveService.FormatName;
    public int SchemaVersion { get; set; } = SchemaMigration.CurrentExportVersion;

    /// <summary>"task", "collection" or "workspace".</summary>
    public string Type { get; set; } = "";

    public DateTimeOffset ExportedUtc { get; set; }
    public string AppVersion { get; set; } = "";

    /// <summary>type "task".</summary>
    public TaskDefinition? Task { get; set; }

    /// <summary>type "collection".</summary>
    public Collection? Collection { get; set; }

    /// <summary>type "collection": its tasks, in order.</summary>
    public List<TaskDefinition>? Tasks { get; set; }

    // ---- type "workspace" ----

    public List<CollectionExport>? Collections { get; set; }
    public List<DatasetExport>? Datasets { get; set; }
    public List<ScheduleEntry>? Schedule { get; set; }
    public AutomataSettings? Settings { get; set; }
    public List<RunExport>? Runs { get; set; }
    public List<ParkedRun>? Parked { get; set; }
}

public sealed class CollectionExport
{
    public Collection Collection { get; set; } = new();
    public List<TaskDefinition> Tasks { get; set; } = [];
}

/// <summary>A dataset as the file it would be: CSV text for a .csv name, a JSON array for .json.</summary>
public sealed class DatasetExport
{
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class RunExport
{
    public RunManifest Manifest { get; set; } = new();
    public List<RunTaskExport> Tasks { get; set; } = [];
}

public sealed class RunTaskExport
{
    public string TaskId { get; set; } = "";
    public List<JsonElement> Events { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> Outputs { get; set; } = [];
}

public sealed record ImportResult(
    IReadOnlyList<Collection> Collections,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyList<string> Warnings)
{
    public int Datasets { get; init; }
    public int ScheduleEntries { get; init; }
    public int Runs { get; init; }
    public int ParkedRuns { get; init; }
    public bool Settings { get; init; }

    /// <summary>"2 collection(s), 5 task(s), 1 dataset(s)…" — only the parts that arrived.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            $"{Collections.Count} collection(s)",
            $"{Tasks.Count} task(s)",
        };
        if (Datasets > 0) parts.Add($"{Datasets} dataset(s)");
        if (ScheduleEntries > 0) parts.Add($"{ScheduleEntries} schedule entr{(ScheduleEntries == 1 ? "y" : "ies")}");
        if (Runs > 0) parts.Add($"{Runs} run(s)");
        if (ParkedRuns > 0) parts.Add($"{ParkedRuns} parked run(s)");
        if (Settings) parts.Add("settings");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Export and import: <c>*.automata.zip</c> for a collection or task, <c>*.automata.json</c> for a
/// task, a collection or the whole workspace. Every import loads the database and none of them
/// overwrites anything: colliding ids are regenerated (and remapped through collectionId,
/// TaskOrder, runTask steps and input wiring), colliding names get " (2)" suffixes, and a task
/// arriving without its collection lands in an on-demand "Imported" collection. Collection and
/// task writes go through <see cref="CollectionStore"/> so its naming and ordering rules apply
/// uniformly.
/// </summary>
public sealed class ArchiveService
{
    public const string FormatName = "automata-export";
    public const string ImportedCollectionName = "Imported";
    public const string JsonExtension = ".automata.json";

    private readonly CollectionStore store;
    private readonly ILogger<ArchiveService> log;
    private readonly DatasetStore? datasets;
    private readonly RunStore? runs;
    private readonly ScheduleStore? schedule;
    private readonly ParkedRunStore? parked;
    private readonly AutomataSettingsStore? settings;

    /// <summary>
    /// The collection store is all a task or collection export needs; the other stores are what a
    /// whole-workspace export/import adds, and without them the workspace carries only
    /// collections and tasks.
    /// </summary>
    public ArchiveService(
        CollectionStore store,
        ILogger<ArchiveService>? log = null,
        DatasetStore? datasets = null,
        RunStore? runs = null,
        ScheduleStore? schedule = null,
        ParkedRunStore? parked = null,
        AutomataSettingsStore? settings = null)
    {
        this.store = store;
        this.log = log ?? NullLogger<ArchiveService>.Instance;
        this.datasets = datasets;
        this.runs = runs;
        this.schedule = schedule;
        this.parked = parked;
        this.settings = settings;
    }

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- zip export ----------------------------------------------------------------------------

    /// <summary>Writes <paramref name="destZipPath"/> (parent dirs created) and returns it.</summary>
    public string ExportCollection(string collectionId, string destZipPath)
    {
        var collection = store.GetCollection(collectionId)
            ?? throw new InvalidOperationException($"Collection '{collectionId}' not found.");
        var tasks = store.LoadTasks(collectionId);

        CreateZip(destZipPath, zip =>
        {
            WriteEntry(zip, "manifest.json", new ExportManifest
            {
                Type = "collection",
                ExportedUtc = DateTimeOffset.UtcNow,
                AppVersion = AppVersion,
            });
            WriteEntry(zip, "collection.json", collection);
            foreach (var task in tasks)
                WriteEntry(zip, $"tasks/{task.Id}.json", task);
        });
        log.LogInformation("Exported collection '{Name}' ({Count} tasks) to {Zip}",
            collection.Name, tasks.Count, destZipPath);
        return destZipPath;
    }

    public string ExportTask(string taskId, string destZipPath)
    {
        var task = store.GetTask(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");

        CreateZip(destZipPath, zip =>
        {
            WriteEntry(zip, "manifest.json", new ExportManifest
            {
                Type = "task",
                ExportedUtc = DateTimeOffset.UtcNow,
                AppVersion = AppVersion,
            });
            WriteEntry(zip, "task.json", task);
        });
        log.LogInformation("Exported task '{Name}' to {Zip}", task.Name, destZipPath);
        return destZipPath;
    }

    /// <summary>Suggested file name for an export, e.g. "email-checks.automata.zip".</summary>
    public static string SuggestedZipName(string displayName) => $"{StoreUtil.Slug(displayName)}.automata.zip";

    /// <summary>Suggested file name for a JSON export, e.g. "email-checks.automata.json".</summary>
    public static string SuggestedJsonName(string displayName) => $"{StoreUtil.Slug(displayName)}{JsonExtension}";

    /// <summary>"automata-workspace-20260923.automata.json".</summary>
    public static string SuggestedWorkspaceName() =>
        $"automata-workspace-{DateTime.Now:yyyyMMdd}{JsonExtension}";

    // ---- JSON export ---------------------------------------------------------------------------

    public string ExportTaskJson(string taskId, string destPath)
    {
        var task = store.GetTask(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        WriteJsonFile(destPath, Envelope("task", e => e.Task = task));
        log.LogInformation("Exported task '{Name}' to {Path}", task.Name, destPath);
        return destPath;
    }

    public string ExportCollectionJson(string collectionId, string destPath)
    {
        var collection = store.GetCollection(collectionId)
            ?? throw new InvalidOperationException($"Collection '{collectionId}' not found.");
        var tasks = store.LoadTasks(collectionId).ToList();
        WriteJsonFile(destPath, Envelope("collection", e =>
        {
            e.Collection = collection;
            e.Tasks = tasks;
        }));
        log.LogInformation("Exported collection '{Name}' ({Count} tasks) to {Path}",
            collection.Name, tasks.Count, destPath);
        return destPath;
    }

    /// <summary>
    /// Everything in the database as one JSON document: collections with their tasks, datasets,
    /// the schedule, settings, run history and parked runs. A backup, and the way to carry a whole
    /// workspace to another machine.
    /// </summary>
    public string ExportWorkspaceJson(string destPath)
    {
        var export = BuildWorkspaceExport();
        WriteJsonFile(destPath, export);
        log.LogInformation("Exported the workspace ({Collections} collections, {Datasets} datasets, {Runs} runs) to {Path}",
            export.Collections!.Count, export.Datasets?.Count ?? 0, export.Runs?.Count ?? 0, destPath);
        return destPath;
    }

    public JsonExport BuildWorkspaceExport() => Envelope("workspace", e =>
    {
        e.Collections = store.LoadCollections()
            .Select(c => new CollectionExport { Collection = c, Tasks = store.LoadTasks(c.Id).ToList() })
            .ToList();
        e.Datasets = datasets?.List()
            .Select(name => new DatasetExport { Name = name, Content = datasets.ExportText(name) })
            .ToList();
        e.Schedule = schedule?.Load();
        e.Settings = settings is { HasSaved: true } ? settings.Load() : null;
        e.Runs = runs?.ListRuns(int.MaxValue)
            .Select(r => new RunExport
            {
                Manifest = r,
                Tasks = runs.TaskIds(r.RunId).Select(taskId => new RunTaskExport
                {
                    TaskId = taskId,
                    Events = runs.LoadEvents(r.RunId, taskId)
                        .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                        .ToList(),
                    Outputs = runs.LoadOutputs(r.RunId, taskId)
                        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                }).ToList(),
            })
            .ToList();
        e.Parked = parked?.List().ToList();
    });

    // ---- import --------------------------------------------------------------------------------

    /// <summary>Imports a <c>.zip</c> or <c>.json</c> export, chosen by extension.</summary>
    public ImportResult Import(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? ImportJson(File.ReadAllText(path))
            : ImportZip(path);

    /// <summary>
    /// Whether this JSON is something <see cref="ImportJson"/> takes: an Automata export, or a bare
    /// task document such as one of the old <c>Documents\Automata\Collections\…\&lt;Task&gt;.json</c>
    /// files. A Chrome DevTools Recorder flow is not — it has no collection id or schema version.
    /// </summary>
    public static bool IsAutomataJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return false;
            if (root["format"]?.GetValueKind() == JsonValueKind.String
                && root["format"]!.GetValue<string>() == FormatName) return true;
            return IsBareTask(root);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBareTask(JsonObject root) =>
        root.ContainsKey("steps") && (root.ContainsKey("collectionId") || root.ContainsKey("schemaVersion"));

    public ImportResult ImportJson(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("Not an Automata export: the file is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not an Automata export: {ex.Message}", ex);
        }

        var warnings = new List<string>();
        if (root["format"] == null && IsBareTask(root))
        {
            // One of the old per-task files, or a task someone saved by hand.
            var bare = Deserialize<TaskDefinition>(json)
                ?? throw new InvalidDataException("The task in this file could not be read.");
            return ImportSingleTaskCore(bare, warnings);
        }

        var export = Deserialize<JsonExport>(json)
            ?? throw new InvalidDataException("Not an Automata export: the file could not be read.");
        if (export.Format != FormatName)
            throw new InvalidDataException($"Not an Automata export: unknown format '{export.Format}'.");
        if (export.SchemaVersion > SchemaMigration.CurrentExportVersion)
            warnings.Add($"Export was written by a newer Automata (schema {export.SchemaVersion}); importing best-effort.");

        return export.Type switch
        {
            "task" => ImportSingleTaskCore(
                export.Task ?? throw new InvalidDataException("Export is missing its task."), warnings),
            "collection" => ImportCollectionCore(
                export.Collection ?? throw new InvalidDataException("Export is missing its collection."),
                export.Tasks ?? [], warnings),
            "workspace" => ImportWorkspace(export, warnings),
            _ => throw new InvalidDataException($"Unknown export type '{export.Type}'."),
        };
    }

    public ImportResult ImportZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var manifest = ReadEntry<ExportManifest>(zip, "manifest.json")
            ?? throw new InvalidDataException("Not an Automata export: manifest.json is missing or unreadable.");
        if (manifest.Format != FormatName)
            throw new InvalidDataException($"Not an Automata export: unknown format '{manifest.Format}'.");

        var warnings = new List<string>();
        if (manifest.SchemaVersion > SchemaMigration.CurrentExportVersion)
            warnings.Add($"Export was written by a newer Automata (schema {manifest.SchemaVersion}); importing best-effort.");

        switch (manifest.Type)
        {
            case "collection":
            {
                var collection = ReadEntry<Collection>(zip, "collection.json")
                    ?? throw new InvalidDataException("Export is missing collection.json.");
                var tasks = zip.Entries
                    .Where(e => e.FullName.StartsWith("tasks/", StringComparison.Ordinal)
                                && e.FullName.EndsWith(".json", StringComparison.Ordinal))
                    .Select(e => ReadEntry<TaskDefinition>(zip, e.FullName))
                    .Where(t => t != null)
                    .Select(t => t!)
                    .ToList();
                return ImportCollectionCore(collection, tasks, warnings);
            }
            case "task":
                return ImportSingleTaskCore(
                    ReadEntry<TaskDefinition>(zip, "task.json")
                        ?? throw new InvalidDataException("Export is missing task.json."),
                    warnings);
            default:
                throw new InvalidDataException($"Unknown export type '{manifest.Type}'.");
        }
    }

    private ImportResult ImportCollectionCore(Collection collection, List<TaskDefinition> tasks, List<string> warnings)
    {
        SchemaMigration.Migrate(collection);
        foreach (var task in tasks) SchemaMigration.Migrate(task);

        var existingCollections = store.LoadCollections();
        var takenTaskIds = TakenTaskIds();

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (store.CollectionIdTaken(collection.Id))
        {
            idMap[collection.Id] = StoreUtil.NewId();
            warnings.Add($"Collection id '{collection.Id}' already exists — imported as a new collection.");
            collection.Id = idMap[collection.Id];
        }
        collection.Name = StoreUtil.UniqueName(collection.Name, existingCollections.Select(c => c.Name));

        // Two passes, because the second one needs the whole map. A collection imported back over
        // itself has EVERY task id taken, so every one is regenerated — and a runTask step or an
        // input wired to another task in the same export has to follow its own copy rather than
        // call whatever was already in the workspace under that id.
        foreach (var task in tasks)
        {
            if (!takenTaskIds.Add(task.Id)) // taken, or twice in this one export
            {
                var fresh = StoreUtil.NewId();
                idMap[task.Id] = fresh;
                task.Id = fresh;
                takenTaskIds.Add(fresh);
            }
        }

        foreach (var task in tasks)
        {
            task.CollectionId = collection.Id;
            StoreUtil.ReidentifySteps(task);
            StoreUtil.RemapTaskIds(task, idMap);
        }

        collection.TaskOrder = collection.TaskOrder
            .Select(id => idMap.GetValueOrDefault(id, id))
            .Where(id => tasks.Any(t => t.Id == id))
            .ToList();

        store.SaveCollection(collection);
        foreach (var task in tasks)
            store.SaveTask(task);

        log.LogInformation("Imported collection '{Name}' with {Count} tasks", collection.Name, tasks.Count);
        return new ImportResult([store.GetCollection(collection.Id)!], tasks, warnings);
    }

    private ImportResult ImportSingleTaskCore(TaskDefinition task, List<string> warnings)
    {
        SchemaMigration.Migrate(task);

        // Orphan task rule: a task never exists without a parent — imports land in "Imported".
        var parent = store.EnsureCollectionNamed(ImportedCollectionName);

        if (store.TaskIdTaken(task.Id))
        {
            warnings.Add($"Task id '{task.Id}' already exists — imported as a new task.");
            task.Id = StoreUtil.NewId();
        }

        task.CollectionId = parent.Id;
        task.Name = StoreUtil.UniqueName(task.Name, store.LoadTasks(parent.Id).Select(t => t.Name));
        StoreUtil.ReidentifySteps(task);
        store.SaveTask(task);

        log.LogInformation("Imported task '{Name}' into '{Collection}'", task.Name, parent.Name);
        return new ImportResult([store.GetCollection(parent.Id)!], [task], warnings);
    }

    /// <summary>
    /// Merges a workspace export into this database without overwriting anything.
    /// <para>
    /// Into an empty database this is a restore: every id, name, timestamp and step id arrives as
    /// it left. Into a populated one the usual rules apply — a collection or task whose id is
    /// taken gets a fresh one (and fresh step ids, because it is a copy), names are suffixed, and
    /// every reference to a regenerated id follows it: runTask steps and input wiring across
    /// collections, schedule targets and chains, run history and parked runs. A dataset whose name
    /// is taken, a run already here, and settings when some are already saved are kept as they
    /// are, with a warning.
    /// </para>
    /// </summary>
    private ImportResult ImportWorkspace(JsonExport export, List<string> warnings)
    {
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var takenTaskIds = TakenTaskIds();
        var regenerated = new HashSet<TaskDefinition>();
        var collectionNames = store.LoadCollections().Select(c => c.Name).ToList();
        var sections = export.Collections ?? [];

        // Pass 1: settle every id before anything is written, so references across collections
        // can be rewritten in pass 2.
        foreach (var section in sections)
        {
            SchemaMigration.Migrate(section.Collection);
            if (store.CollectionIdTaken(section.Collection.Id))
            {
                var fresh = StoreUtil.NewId();
                idMap[section.Collection.Id] = fresh;
                section.Collection.Id = fresh;
            }
            foreach (var task in section.Tasks)
            {
                SchemaMigration.Migrate(task);
                if (takenTaskIds.Add(task.Id)) continue;
                var fresh = StoreUtil.NewId();
                idMap[task.Id] = fresh;
                task.Id = fresh;
                takenTaskIds.Add(fresh);
                regenerated.Add(task);
            }
        }

        var importedCollections = new List<Collection>();
        var importedTasks = new List<TaskDefinition>();
        foreach (var section in sections)
        {
            var collection = section.Collection;
            var wanted = collection.Name;
            collection.Name = StoreUtil.UniqueName(wanted, collectionNames);
            if (collection.Name != wanted)
                warnings.Add($"A collection named '{wanted}' already exists — imported as '{collection.Name}'.");
            collectionNames.Add(collection.Name);

            foreach (var task in section.Tasks)
            {
                task.CollectionId = collection.Id;
                if (regenerated.Contains(task)) StoreUtil.ReidentifySteps(task);
                StoreUtil.RemapTaskIds(task, idMap);
            }
            collection.TaskOrder = collection.TaskOrder
                .Select(id => idMap.GetValueOrDefault(id, id))
                .Where(id => section.Tasks.Any(t => t.Id == id))
                .ToList();

            store.InsertVerbatim(collection, section.Tasks);
            importedCollections.Add(collection);
            importedTasks.AddRange(section.Tasks);
        }
        if (idMap.Count > 0)
            warnings.Add($"{idMap.Count} id(s) were already in use and were regenerated.");

        var datasetCount = 0;
        foreach (var dataset in export.Datasets ?? [])
        {
            if (datasets == null) break;
            if (datasets.Exists(dataset.Name))
            {
                warnings.Add($"Dataset '{dataset.Name}' already exists — kept the existing one.");
                continue;
            }
            datasets.ImportText(dataset.Name, dataset.Content);
            datasetCount++;
        }

        var scheduleCount = 0;
        if (schedule != null && export.Schedule is { Count: > 0 } entries)
        {
            var takenEntryIds = schedule.Load().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var entryMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (!takenEntryIds.Contains(entry.Id)) continue;
                entryMap[entry.Id] = StoreUtil.NewId();
                entry.Id = entryMap[entry.Id];
            }
            foreach (var entry in entries)
            {
                entry.TargetId = idMap.GetValueOrDefault(entry.TargetId, entry.TargetId);
                foreach (var trigger in entry.Triggers)
                    if (trigger.AfterEntryId != null)
                        trigger.AfterEntryId = entryMap.GetValueOrDefault(trigger.AfterEntryId, trigger.AfterEntryId);
                schedule.Upsert(entry);
                scheduleCount++;
            }
        }

        var runCount = 0;
        foreach (var run in export.Runs ?? [])
        {
            if (runs == null) break;
            run.Manifest.TargetId = idMap.GetValueOrDefault(run.Manifest.TargetId, run.Manifest.TargetId);
            var tasks = run.Tasks.Select(t => new ImportedRunTask(
                idMap.GetValueOrDefault(t.TaskId, t.TaskId),
                t.Events.Select(e => e.GetRawText()).ToList(),
                t.Outputs));
            if (runs.ImportRun(run.Manifest, tasks)) runCount++;
        }

        var parkedCount = 0;
        foreach (var waiting in export.Parked ?? [])
        {
            if (parked == null) break;
            if (parked.Get(waiting.RunId) != null) continue;
            waiting.TaskId = idMap.GetValueOrDefault(waiting.TaskId, waiting.TaskId);
            waiting.CollectionId = idMap.GetValueOrDefault(waiting.CollectionId, waiting.CollectionId);
            waiting.RemainingTaskIds = waiting.RemainingTaskIds.Select(id => idMap.GetValueOrDefault(id, id)).ToList();
            parked.Save(waiting);
            parkedCount++;
        }

        var settingsImported = false;
        if (settings != null && export.Settings != null)
        {
            if (settings.HasSaved)
            {
                warnings.Add("Settings were left as they are here — this workspace already has its own.");
            }
            else
            {
                settings.Save(export.Settings);
                settingsImported = true;
            }
        }

        var result = new ImportResult(importedCollections, importedTasks, warnings)
        {
            Datasets = datasetCount,
            ScheduleEntries = scheduleCount,
            Runs = runCount,
            ParkedRuns = parkedCount,
            Settings = settingsImported,
        };
        log.LogInformation("Imported a workspace: {What}", result.Describe());
        return result;
    }

    // ---- plumbing ------------------------------------------------------------------------------

    /// <summary>Every task id already in the database, hidden ones included — a hidden row still
    /// owns its key.</summary>
    private HashSet<string> TakenTaskIds()
    {
        using var db = store.Database.CreateDbContext();
        return Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .IgnoreQueryFilters(db.Tasks)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static JsonExport Envelope(string type, Action<JsonExport> fill)
    {
        var export = new JsonExport { Type = type, ExportedUtc = DateTimeOffset.UtcNow, AppVersion = AppVersion };
        fill(export);
        return export;
    }

    private static void WriteJsonFile<T>(string destPath, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(destPath, JsonSerializer.Serialize(value, AutomataJson.Options));
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, AutomataJson.Options); }
        catch (JsonException ex) { throw new InvalidDataException($"The export could not be read: {ex.Message}", ex); }
    }

    private static void CreateZip(string destZipPath, Action<ZipArchive> fill)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destZipPath));
        if (dir != null) Directory.CreateDirectory(dir);
        using var stream = File.Create(destZipPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        fill(zip);
    }

    private static void WriteEntry<T>(ZipArchive zip, string name, T value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(JsonSerializer.Serialize(value, AutomataJson.Options));
    }

    private static T? ReadEntry<T>(ZipArchive zip, string name) where T : class
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open());
        try { return JsonSerializer.Deserialize<T>(reader.ReadToEnd(), AutomataJson.Options); }
        catch (JsonException) { return null; }
    }
}

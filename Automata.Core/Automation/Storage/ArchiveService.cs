using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>Envelope identifying a zip as an Automata export.</summary>
public sealed class ExportManifest
{
    public string Format { get; set; } = "automata-export";
    public int SchemaVersion { get; set; } = SchemaMigration.CurrentExportVersion;

    /// <summary>"collection" or "task".</summary>
    public string Type { get; set; } = "";

    public DateTimeOffset ExportedUtc { get; set; }
    public string AppVersion { get; set; } = "";
}

public sealed record ImportResult(
    IReadOnlyList<Collection> Collections,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Zip export/import of collections and single tasks. Imports never overwrite: colliding ids are
/// regenerated (and remapped through collectionId/TaskOrder), colliding names get " (2)" suffixes,
/// and a task arriving without its collection lands in an on-demand "Imported" collection. All
/// writes go through <see cref="CollectionStore"/> so its healing/ordering rules apply uniformly.
/// </summary>
public sealed class ArchiveService
{
    public const string ImportedCollectionName = "Imported";

    private readonly CollectionStore store;
    private readonly ILogger<ArchiveService> log;

    public ArchiveService(CollectionStore store, ILogger<ArchiveService>? log = null)
    {
        this.store = store;
        this.log = log ?? NullLogger<ArchiveService>.Instance;
    }

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ---- export ------------------------------------------------------------------------------

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

    // ---- import ------------------------------------------------------------------------------

    public ImportResult Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var manifest = ReadEntry<ExportManifest>(zip, "manifest.json")
            ?? throw new InvalidDataException("Not an Automata export: manifest.json is missing or unreadable.");
        if (manifest.Format != "automata-export")
            throw new InvalidDataException($"Not an Automata export: unknown format '{manifest.Format}'.");

        var warnings = new List<string>();
        if (manifest.SchemaVersion > SchemaMigration.CurrentExportVersion)
            warnings.Add($"Export was written by a newer Automata (schema {manifest.SchemaVersion}); importing best-effort.");

        return manifest.Type switch
        {
            "collection" => ImportCollection(zip, warnings),
            "task" => ImportSingleTask(zip, warnings),
            _ => throw new InvalidDataException($"Unknown export type '{manifest.Type}'."),
        };
    }

    private ImportResult ImportCollection(ZipArchive zip, List<string> warnings)
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

        var existingCollections = store.LoadCollections();
        var existingTaskIds = existingCollections
            .SelectMany(c => store.LoadTasks(c.Id))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);

        if (existingCollections.Any(c => c.Id == collection.Id))
        {
            idMap[collection.Id] = StoreUtil.NewId();
            warnings.Add($"Collection id '{collection.Id}' already exists — imported as a new collection.");
            collection.Id = idMap[collection.Id];
        }
        collection.Name = StoreUtil.UniqueName(collection.Name, existingCollections.Select(c => c.Name));

        // Two passes, because the second one needs the whole map. A collection imported back over
        // itself has EVERY task id taken, so every one is regenerated — and a runTask step or an
        // input wired to another task in the same zip has to follow its own copy rather than call
        // whatever was already in the workspace under that id.
        foreach (var task in tasks)
        {
            if (!existingTaskIds.Contains(task.Id)) continue;
            var fresh = StoreUtil.NewId();
            idMap[task.Id] = fresh;
            task.Id = fresh;
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

        // A task in the zip whose collection pointer was foreign got re-pointed above; a zip with
        // stray tasks/ entries but no collection.json never reaches here (throw above).
        log.LogInformation("Imported collection '{Name}' with {Count} tasks", collection.Name, tasks.Count);
        return new ImportResult([collection], tasks, warnings);
    }

    private ImportResult ImportSingleTask(ZipArchive zip, List<string> warnings)
    {
        var task = ReadEntry<TaskDefinition>(zip, "task.json")
            ?? throw new InvalidDataException("Export is missing task.json.");

        // Orphan task rule: a task never exists without a parent — imports land in "Imported".
        var parent = store.EnsureCollectionNamed(ImportedCollectionName);

        var allTaskIds = store.LoadCollections()
            .SelectMany(c => store.LoadTasks(c.Id))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (allTaskIds.Contains(task.Id))
        {
            warnings.Add($"Task id '{task.Id}' already exists — imported as a new task.");
            task.Id = StoreUtil.NewId();
        }

        task.CollectionId = parent.Id;
        task.Name = StoreUtil.UniqueName(task.Name, store.LoadTasks(parent.Id).Select(t => t.Name));
        StoreUtil.ReidentifySteps(task);
        store.SaveTask(task);

        log.LogInformation("Imported task '{Name}' into '{Collection}'", task.Name, parent.Name);
        return new ImportResult([parent], [task], warnings);
    }

    // ---- plumbing ----------------------------------------------------------------------------

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

using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Data;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Where Automata kept things before the database, each overridable by the same AUTOMATA_*
/// variable that used to point the store there — so a harness's scratch folders are what gets
/// imported, never the developer's real Documents.
/// </summary>
public sealed record LegacyLocations(
    string CollectionsRoot,
    string DatasetsRoot,
    string RunsRoot,
    string SchedulePath,
    string ParkedRoot,
    string SettingsPath)
{
    private static string Documents(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", .. parts]);

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    public static LegacyLocations FromEnvironment() => new(
        Env("AUTOMATA_COLLECTIONS_ROOT", Documents("Collections")),
        Env("AUTOMATA_DATASETS_ROOT", Documents("Datasets")),
        Env("AUTOMATA_RUNS_ROOT", Documents("Runs")),
        Env("AUTOMATA_SCHEDULE_PATH", Documents("Schedule", "schedule.json")),
        Env("AUTOMATA_PARKED_ROOT", Documents("Parked")),
        Env("AUTOMATA_SETTINGS_PATH", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MindAttic", "Automata", "settings.json")));

    /// <summary>The same layout under one folder — what the tests build.</summary>
    public static LegacyLocations Under(string root) => new(
        Path.Combine(root, "Collections"),
        Path.Combine(root, "Datasets"),
        Path.Combine(root, "Runs"),
        Path.Combine(root, "Schedule", "schedule.json"),
        Path.Combine(root, "Parked"),
        Path.Combine(root, "settings.json"));

    public bool AnyExist =>
        Directory.Exists(CollectionsRoot) || Directory.Exists(DatasetsRoot) || Directory.Exists(RunsRoot)
        || File.Exists(SchedulePath) || Directory.Exists(ParkedRoot) || File.Exists(SettingsPath);
}

/// <summary>What the one-time import did — also what is recorded in the database's Meta table.</summary>
public sealed record LegacyImportReport(
    bool Ran,
    string Outcome,
    int Collections,
    int Tasks,
    int Datasets,
    int ScheduleEntries,
    int Runs,
    int ParkedRuns,
    bool Settings,
    IReadOnlyList<string> Warnings)
{
    public DateTimeOffset AtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Describe() => Ran
        ? $"Imported {Collections} collection(s), {Tasks} task(s), {Datasets} dataset(s), " +
          $"{ScheduleEntries} schedule entr{(ScheduleEntries == 1 ? "y" : "ies")}, {Runs} run(s), " +
          $"{ParkedRuns} parked run(s){(Settings ? " and settings" : "")} from the old JSON files " +
          "(left in place as a backup)."
        : Outcome;
}

/// <summary>
/// The one-time move off the JSON files: on the first launch against an empty database, reads
/// <c>Documents\Automata\{Collections,Datasets,Runs,Schedule,Parked}</c> and the old
/// <c>%APPDATA%\MindAttic\Automata\settings.json</c> into the database.
/// <para>
/// <b>The files are never touched.</b> They stay exactly where they are as a backup: this only
/// reads. That is also why the old store's "heal a hand edit" rules live on here, applied to what
/// is imported instead of rewritten on disk — a folder renamed in Explorer names its collection, a
/// task file renamed by hand names its task, a copy-pasted file with a duplicate id gets a fresh
/// one, and a folder of tasks missing its collection.json is recovered under the folder's name.
/// </para>
/// <para>
/// It runs at most once per database. The outcome — including "nothing to import" and "the
/// database already had data" — is recorded in the <c>Meta</c> table, so a later launch never
/// re-imports and a user who deletes something never sees it come back from the old files.
/// </para>
/// </summary>
public sealed class LegacyWorkspaceImporter
{
    public const string MarkerKey = "legacy-json-import";
    private const string ManifestFileName = "collection.json";

    private readonly AutomataDatabase database;
    private readonly LegacyLocations locations;
    private readonly ILogger log;

    public LegacyWorkspaceImporter(AutomataDatabase database, LegacyLocations? locations = null, ILogger? log = null)
    {
        this.database = database;
        this.locations = locations ?? LegacyLocations.FromEnvironment();
        this.log = log ?? NullLogger.Instance;
    }

    /// <summary>The recorded outcome of the import, or null if it has never been considered.</summary>
    public LegacyImportReport? Recorded()
    {
        using var db = database.CreateDbContext();
        var entry = db.Meta.AsNoTracking().FirstOrDefault(m => m.Key == MarkerKey);
        return entry == null ? null : JsonSerializer.Deserialize<LegacyImportReport>(entry.Value, AutomataJson.Compact);
    }

    /// <summary>Imports the old files if, and only if, this database has never considered them
    /// and is empty. Safe to call on every launch.</summary>
    public LegacyImportReport ImportOnce()
    {
        // The app and the runner can both be starting against a fresh database; only one of them
        // may import.
        using var _ = ExclusiveFileLock.Acquire(database.LockKey("legacy-import"), TimeSpan.FromMinutes(2));

        if (Recorded() is { } previous)
            return previous with { Ran = false, Outcome = $"Already considered on {previous.AtUtc:u}: {previous.Outcome}" };

        LegacyImportReport report;
        if (!IsEmpty())
        {
            report = Nothing("The database already had data, so the old JSON files were not imported.");
        }
        else if (!locations.AnyExist)
        {
            report = Nothing("No old JSON files were found — nothing to import.");
        }
        else
        {
            report = Import();
            // Old folders that turned out to hold nothing (an empty Collections folder is enough to
            // get here) are the same as none at all — not an import worth announcing.
            if (report is { Collections: 0, Tasks: 0, Datasets: 0, ScheduleEntries: 0, Runs: 0, ParkedRuns: 0, Settings: false, Warnings.Count: 0 })
                report = Nothing("No old JSON files were found — nothing to import.");
            else
            {
                log.LogInformation("{Summary}", report.Describe());
                foreach (var warning in report.Warnings) log.LogWarning("Legacy import: {Warning}", warning);
            }
        }

        Record(report);
        return report;
    }

    private static LegacyImportReport Nothing(string why) => new(false, why, 0, 0, 0, 0, 0, 0, false, []);

    private bool IsEmpty()
    {
        using var db = database.CreateDbContext();
        return !db.Collections.IgnoreQueryFilters().Any() && !db.Datasets.Any() && !db.Runs.Any()
               && !db.Schedule.Any() && !db.ParkedRuns.Any() && !db.Settings.Any();
    }

    private void Record(LegacyImportReport report)
    {
        using var db = database.CreateDbContext();
        db.Meta.Add(new MetaEntry
        {
            Key = MarkerKey,
            Value = JsonSerializer.Serialize(report, AutomataJson.Compact),
            UpdatedUtc = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private LegacyImportReport Import()
    {
        var warnings = new List<string>();
        var (collections, tasks) = Guard(warnings, "collections", ImportCollections, (0, 0));
        var datasets = Guard(warnings, "datasets", ImportDatasets, 0);
        var schedule = Guard(warnings, "the schedule", ImportSchedule, 0);
        var runs = Guard(warnings, "run history", ImportRuns, 0);
        var parked = Guard(warnings, "parked runs", ImportParked, 0);
        var settings = Guard(warnings, "settings", ImportSettings, false);
        return new LegacyImportReport(true, "Imported the old JSON files.",
            collections, tasks, datasets, schedule, runs, parked, settings, warnings);
    }

    /// <summary>One part failing must not cost the rest of the import.</summary>
    private T Guard<T>(List<string> warnings, string what, Func<List<string>, T> part, T fallback)
    {
        try { return part(warnings); }
        catch (Exception ex)
        {
            warnings.Add($"Could not import {what}: {ex.Message}");
            return fallback;
        }
    }

    // ---- collections and tasks ---------------------------------------------------------------

    private (int Collections, int Tasks) ImportCollections(List<string> warnings)
    {
        if (!Directory.Exists(locations.CollectionsRoot)) return (0, 0);
        var store = new CollectionStore(database);

        var seenCollectionIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        int collections = 0, tasks = 0;

        foreach (var dir in Directory.EnumerateDirectories(locations.CollectionsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var collection = ReadCollectionDir(dir, warnings);
            if (collection == null) continue;
            if (!seenCollectionIds.Add(collection.Id))
            {
                collection.Id = StoreUtil.NewId(); // Explorer copy-paste duplicate
                seenCollectionIds.Add(collection.Id);
            }
            collection.Name = StoreUtil.UniqueName(collection.Name, names);
            names.Add(collection.Name);

            var loaded = new List<TaskDefinition>();
            var taskNames = new List<string>();
            foreach (var file in TaskFiles(dir))
            {
                var task = ReadJson<TaskDefinition>(file);
                if (task == null)
                {
                    warnings.Add($"Skipped unreadable task file {file}");
                    continue;
                }
                SchemaMigration.Migrate(task);

                if (!seenTaskIds.Add(task.Id))
                {
                    task.Id = StoreUtil.NewId(); // copy-pasted file: give it its own identity
                    seenTaskIds.Add(task.Id);
                }
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!string.Equals(SafeName(task.Name), fileName, StringComparison.OrdinalIgnoreCase))
                    task.Name = fileName; // renamed in Explorer — the file name wins
                task.Name = StoreUtil.UniqueName(task.Name, taskNames);
                taskNames.Add(task.Name);
                task.CollectionId = collection.Id; // the folder it sits in is its collection
                loaded.Add(task);
            }

            var ordered = CollectionStore.Ordered(loaded, collection.TaskOrder);
            collection.TaskOrder = ordered.Select(t => t.Id).ToList();
            store.InsertVerbatim(collection, ordered);
            collections++;
            tasks += ordered.Count;
        }
        return (collections, tasks);
    }

    private static Collection? ReadCollectionDir(string dir, List<string> warnings)
    {
        var folderName = Path.GetFileName(dir);
        var file = Path.Combine(dir, ManifestFileName);

        if (!File.Exists(file))
        {
            // A folder of task files someone hand-copied in: give it a collection so they surface.
            if (!TaskFiles(dir).Any()) return null;
            warnings.Add($"Collection folder {dir} has no collection.json — imported under its folder name.");
            return new Collection
            {
                Name = folderName,
                CreatedUtc = DateTimeOffset.UtcNow,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };
        }

        var collection = ReadJson<Collection>(file);
        if (collection == null)
        {
            warnings.Add($"Skipped unreadable collection file {file}");
            return null;
        }
        SchemaMigration.Migrate(collection);
        if (!string.Equals(SafeName(collection.Name), folderName, StringComparison.OrdinalIgnoreCase))
            collection.Name = folderName; // renamed in Explorer — the folder wins
        return collection;
    }

    private static IEnumerable<string> TaskFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : [];

    // Names Windows refuses (device names) plus "collection", which would have collided with the
    // manifest file had a task been named that.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "collection",
    };

    /// <summary>
    /// The old store's display name → file/folder name projection. Needed only to tell a real
    /// Explorer rename from a name that was merely sanitised on its way to disk: the JSON kept the
    /// original name verbatim, and a sanitisation difference must never count as a rename.
    /// </summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "Unnamed";
        if (cleaned.Length > 100) cleaned = cleaned[..100].TrimEnd('.', ' ');
        if (ReservedNames.Contains(cleaned)) cleaned = "_" + cleaned;
        return cleaned;
    }

    // ---- everything else ---------------------------------------------------------------------

    private int ImportDatasets(List<string> warnings)
    {
        if (!Directory.Exists(locations.DatasetsRoot)) return 0;
        var store = new DatasetStore(database);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(locations.DatasetsRoot)
                     .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                store.ImportFile(file);
                count++;
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                warnings.Add($"Skipped dataset {file}: {ex.Message}");
            }
        }
        return count;
    }

    private int ImportSchedule(List<string> warnings)
    {
        if (!File.Exists(locations.SchedulePath)) return 0;
        var entries = ReadJson<List<ScheduleEntry>>(locations.SchedulePath);
        if (entries == null)
        {
            warnings.Add($"Skipped unreadable schedule {locations.SchedulePath}");
            return 0;
        }
        var unique = entries.GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        new ScheduleStore(database).Save(unique);
        return unique.Count;
    }

    private int ImportRuns(List<string> warnings)
    {
        if (!Directory.Exists(locations.RunsRoot)) return 0;
        var store = new RunStore(database);
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(locations.RunsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = ReadJson<RunManifest>(Path.Combine(dir, "manifest.json"));
            if (manifest == null || string.IsNullOrEmpty(manifest.RunId))
            {
                warnings.Add($"Skipped run folder without a readable manifest: {dir}");
                continue;
            }

            var tasks = new List<ImportedRunTask>();
            var tasksDir = Path.Combine(dir, "tasks");
            if (Directory.Exists(tasksDir))
            {
                foreach (var taskDir in Directory.EnumerateDirectories(tasksDir))
                {
                    var events = Path.Combine(taskDir, "events.jsonl");
                    var lines = File.Exists(events)
                        ? File.ReadAllLines(events).Where(l => l.Trim().Length > 0).ToList()
                        : [];
                    var outputs = ReadJson<Dictionary<string, Dictionary<string, string>>>(
                        Path.Combine(taskDir, "outputs.json")) ?? [];
                    tasks.Add(new ImportedRunTask(Path.GetFileName(taskDir), lines, outputs));
                }
            }
            if (store.ImportRun(manifest, tasks)) count++;
        }
        return count;
    }

    private int ImportParked(List<string> warnings)
    {
        if (!Directory.Exists(locations.ParkedRoot)) return 0;
        var store = new ParkedRunStore(database);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(locations.ParkedRoot, "*.json"))
        {
            var parked = ReadJson<ParkedRun>(file);
            if (parked == null || string.IsNullOrEmpty(parked.RunId))
            {
                warnings.Add($"Skipped unreadable parked run {file}");
                continue;
            }
            store.Save(parked);
            count++;
        }
        return count;
    }

    private bool ImportSettings(List<string> warnings)
    {
        if (!File.Exists(locations.SettingsPath)) return false;
        var settings = ReadJson<AutomataSettings>(locations.SettingsPath);
        if (settings == null)
        {
            warnings.Add($"Skipped unreadable settings {locations.SettingsPath}");
            return false;
        }
        settings.Theme = AutomataSettings.Themes.Coerce(settings.Theme);
        new AutomataSettingsStore(database).Save(settings);
        return true;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), AutomataJson.Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

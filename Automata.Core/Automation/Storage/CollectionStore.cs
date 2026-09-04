using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// File-backed store for collections and tasks, laid out for humans browsing it in Explorer:
/// <code>
/// %USERPROFILE%\Documents\Automata\Collections\&lt;Collection Name&gt;\collection.json
/// %USERPROFILE%\Documents\Automata\Collections\&lt;Collection Name&gt;\&lt;Task Name&gt;.json
/// </code>
/// Folder and file names mirror the display names (sanitized for the filesystem); the ids inside
/// the JSON remain the stable identity, so renames are just folder/file moves. The store heals
/// hand-edits on load: a folder or file renamed in Explorer wins (the JSON name is updated to
/// match), a copy-pasted folder/file with a duplicate id gets a fresh id, a task folder missing
/// collection.json gets one recovered from the folder name — files never silently vanish.
/// </summary>
public sealed class CollectionStore
{
    public const string DefaultCollectionName = "Default";
    private const string ManifestFileName = "collection.json";

    private readonly ILogger<CollectionStore> log;

    /// <summary>
    /// Serialises writes of one task. A save is a read-modify-write across sibling files - find
    /// this task's file, check whether a rename would land on another one, move it, write it - and
    /// two of those interleaving would leave a half-written file behind. Nothing needed it until a
    /// run could save on its own: a parallel loop whose rows call a task that heals will ask two
    /// threads to write the same file at the same moment.
    /// </summary>
    private readonly Lock saveGate = new();

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automata", "Collections");

    public string RootPath { get; }

    public CollectionStore(string? rootPath = null, ILogger<CollectionStore>? log = null)
    {
        RootPath = rootPath ?? DefaultRoot;
        this.log = log ?? NullLogger<CollectionStore>.Instance;
    }

    // ---- collections -------------------------------------------------------------------------

    public IReadOnlyList<Collection> LoadCollections()
    {
        if (!Directory.Exists(RootPath)) return [];

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var collections = new List<Collection>();
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var collection = LoadCollectionFromDir(dir, seenIds);
            if (collection == null) continue;
            seenIds.Add(collection.Id);
            collections.Add(collection);
        }
        return collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Collection? GetCollection(string id) =>
        LoadCollections().FirstOrDefault(c => c.Id == id);

    public Collection CreateCollection(string name)
    {
        var collection = new Collection
        {
            Name = StoreUtil.UniqueName(name, LoadCollections().Select(c => c.Name)),
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };
        SaveCollection(collection);
        return collection;
    }

    public void SaveCollection(Collection collection)
    {
        collection.ModifiedUtc = DateTimeOffset.UtcNow;

        var existingDir = FindCollectionDirById(collection.Id);
        var targetDir = Path.Combine(RootPath, SafeName(collection.Name));

        // Renaming onto an occupied folder: keep both, suffix this one. An occupant whose
        // manifest is missing or unreadable is treated as foreign too — the read path recovers
        // corrupt files with a warning, so the write path must never silently clobber them.
        if (Directory.Exists(targetDir) && !PathsEqual(existingDir, targetDir))
        {
            var occupant = ReadJson<Collection>(Path.Combine(targetDir, ManifestFileName));
            if (occupant == null || occupant.Id != collection.Id)
            {
                collection.Name = UniqueBySafeName(collection.Name,
                    Directory.EnumerateDirectories(RootPath).Select(Path.GetFileName));
                targetDir = Path.Combine(RootPath, SafeName(collection.Name));
            }
        }

        if (existingDir != null && !PathsEqual(existingDir, targetDir))
            Directory.Move(existingDir, targetDir);
        Directory.CreateDirectory(targetDir);
        WriteJson(Path.Combine(targetDir, ManifestFileName), collection);
    }

    public void DeleteCollection(string id)
    {
        var dir = FindCollectionDirById(id);
        if (dir != null) Directory.Delete(dir, recursive: true);
    }

    public Collection DuplicateCollection(string id)
    {
        var source = GetCollection(id)
            ?? throw new InvalidOperationException($"Collection '{id}' not found.");
        var tasks = LoadTasks(id);

        var copy = StoreUtil.Clone(source);
        copy.Id = StoreUtil.NewId();
        copy.Name = StoreUtil.UniqueName(source.Name, LoadCollections().Select(c => c.Name));
        copy.CreatedUtc = DateTimeOffset.UtcNow;
        copy.TaskOrder = [];
        SaveCollection(copy);

        foreach (var task in tasks)
        {
            var taskCopy = StoreUtil.Clone(task);
            taskCopy.Id = StoreUtil.NewId();
            taskCopy.CollectionId = copy.Id;
            StoreUtil.RegenerateStepIds(taskCopy.Steps);
            SaveTask(taskCopy);
        }
        return copy;
    }

    /// <summary>The collection tasks land in when saved without a parent (created on demand).</summary>
    public Collection EnsureDefaultCollection() => EnsureCollectionNamed(DefaultCollectionName);

    /// <summary>Find a collection by exact name (case-insensitive) or create it.</summary>
    public Collection EnsureCollectionNamed(string name)
    {
        var existing = LoadCollections()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var collection = new Collection
        {
            Name = name,
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };
        SaveCollection(collection);
        return collection;
    }

    // ---- tasks -------------------------------------------------------------------------------

    /// <summary>Tasks of one collection, ordered by its TaskOrder; unlisted tasks sort last by name.</summary>
    public IReadOnlyList<TaskDefinition> LoadTasks(string collectionId)
    {
        var dir = FindCollectionDirById(collectionId);
        if (dir == null) return [];
        var collection = ReadJson<Collection>(Path.Combine(dir, ManifestFileName));
        if (collection == null) return [];

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<TaskDefinition>();
        foreach (var file in TaskFiles(dir))
        {
            var task = ReadJson<TaskDefinition>(file);
            if (task == null)
            {
                log.LogWarning("Skipping unreadable task file {File}", file);
                continue;
            }
            SchemaMigration.Migrate(task);

            var changed = false;
            if (!seenIds.Add(task.Id))
            {
                task.Id = StoreUtil.NewId(); // Explorer copy-paste duplicate — give it its own identity
                seenIds.Add(task.Id);
                changed = true;
            }
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(SafeName(task.Name), fileName, StringComparison.OrdinalIgnoreCase))
            {
                task.Name = fileName; // renamed in Explorer — the file name wins
                changed = true;
            }
            if (task.CollectionId != collection.Id)
            {
                task.CollectionId = collection.Id; // moved between folders by hand — the folder wins
                changed = true;
            }
            if (changed) WriteJson(file, task);
            tasks.Add(task);
        }

        var order = collection.TaskOrder;
        return tasks
            .OrderBy(t => { var i = order.IndexOf(t.Id); return i < 0 ? int.MaxValue : i; })
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TaskDefinition? GetTask(string taskId)
    {
        foreach (var collection in LoadCollections())
        {
            var task = LoadTasks(collection.Id).FirstOrDefault(t => t.Id == taskId);
            if (task != null) return task;
        }
        return null;
    }

    /// <summary>
    /// Persist a task. An empty CollectionId gets the default collection assigned — a task never
    /// exists without a parent collection. A renamed task's file moves with it.
    /// </summary>
    public void SaveTask(TaskDefinition task)
    {
        lock (saveGate) SaveTaskCore(task);
    }

    private void SaveTaskCore(TaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.CollectionId))
            task.CollectionId = EnsureDefaultCollection().Id;

        var dir = FindCollectionDirById(task.CollectionId)
            ?? throw new InvalidOperationException($"Collection '{task.CollectionId}' not found.");
        var collection = ReadJson<Collection>(Path.Combine(dir, ManifestFileName))!;

        if (task.CreatedUtc == default) task.CreatedUtc = DateTimeOffset.UtcNow;
        task.ModifiedUtc = DateTimeOffset.UtcNow;

        var existingFile = FindTaskFileById(dir, task.Id);
        var target = Path.Combine(dir, SafeName(task.Name) + ".json");

        // Renaming onto a sibling task's file: keep both, suffix this one. An UNREADABLE
        // occupant is foreign by definition — never silently overwrite a file that might still
        // be recoverable by hand.
        if (File.Exists(target) && !PathsEqual(existingFile, target))
        {
            var occupant = ReadJson<TaskDefinition>(target);
            if (occupant == null || occupant.Id != task.Id)
            {
                task.Name = UniqueBySafeName(task.Name, TaskFiles(dir)
                    .Where(f => !PathsEqual(f, existingFile))
                    .Select(Path.GetFileNameWithoutExtension));
                target = Path.Combine(dir, SafeName(task.Name) + ".json");
            }
        }

        if (existingFile != null && !PathsEqual(existingFile, target))
            File.Move(existingFile, target);
        WriteJson(target, task);

        if (!collection.TaskOrder.Contains(task.Id))
        {
            collection.TaskOrder.Add(task.Id);
            SaveCollection(collection);
        }
    }

    public void DeleteTask(string taskId)
    {
        foreach (var dir in CollectionDirs())
        {
            var file = FindTaskFileById(dir, taskId);
            if (file == null) continue;

            File.Delete(file);
            var collection = ReadJson<Collection>(Path.Combine(dir, ManifestFileName));
            if (collection != null && collection.TaskOrder.Remove(taskId))
                SaveCollection(collection);
            return;
        }
    }

    /// <summary>
    /// Moves a task to another collection.
    /// <para>
    /// A generated example that leaves the Demos collection stops being one: it loses its demo
    /// marker and takes a fresh id. Moving it out is exactly how someone keeps a version of their
    /// own — the generator restores everything it still owns, so the copy has to stop being owned.
    /// Keeping the marker would also leave the fixed demo id on two tasks the moment the generator
    /// wrote the example back.
    /// </para>
    /// </summary>
    public TaskDefinition MoveTask(string taskId, string toCollectionId)
    {
        var task = GetTask(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        if (task.CollectionId == toCollectionId) return task;
        _ = GetCollection(toCollectionId)
            ?? throw new InvalidOperationException($"Collection '{toCollectionId}' not found.");

        DeleteTask(taskId);
        task.CollectionId = toCollectionId;
        if (task.Demo != null)
        {
            task.Demo = null;
            task.Id = StoreUtil.NewId();
        }
        task.Name = StoreUtil.UniqueName(task.Name, LoadTasks(toCollectionId).Select(t => t.Name));
        SaveTask(task);
        return task;
    }

    public TaskDefinition DuplicateTask(string taskId)
    {
        var source = GetTask(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");

        var copy = StoreUtil.Clone(source);
        copy.Id = StoreUtil.NewId();
        copy.Name = StoreUtil.UniqueName(source.Name, LoadTasks(source.CollectionId).Select(t => t.Name));
        copy.CreatedUtc = DateTimeOffset.UtcNow;
        // A copy of an example is not the example. Two tasks answering to one demo key would leave
        // the generator restoring whichever it found first and silently leaving the other behind.
        copy.Demo = null;
        StoreUtil.RegenerateStepIds(copy.Steps);
        SaveTask(copy);
        return copy;
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private IEnumerable<string> CollectionDirs() =>
        Directory.Exists(RootPath) ? Directory.EnumerateDirectories(RootPath) : [];

    private static IEnumerable<string> TaskFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Where(f =>
                !string.Equals(Path.GetFileName(f), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            : [];

    // ---- finding a thing by its id ------------------------------------------------------------
    //
    // Both of these used to answer by reading EVERY file: a save walked every collection manifest
    // to find its folder, then every task file in that folder to find its own. That is fine at ten
    // tasks and quadratic-feeling at a few hundred — and it happens on every keystroke the step
    // editor commits.
    //
    // So the answers are remembered. What they are NOT is trusted: this store is a folder a person
    // is invited to rearrange in Explorer, and a cache that believed itself would hand back a path
    // to a file somebody has since renamed, moved or replaced. Every hit is confirmed by reading
    // the id back out of the file it points at — one small read instead of a whole directory —
    // and a confirmation that fails simply falls through to the scan that populated it.

    private readonly ConcurrentDictionary<string, string> collectionDirById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> taskFileById = new(StringComparer.Ordinal);

    private string? FindCollectionDirById(string id)
    {
        if (collectionDirById.TryGetValue(id, out var remembered)
            && ReadJson<Collection>(Path.Combine(remembered, ManifestFileName))?.Id == id)
        {
            return remembered;
        }

        foreach (var dir in CollectionDirs())
        {
            var found = ReadJson<Collection>(Path.Combine(dir, ManifestFileName))?.Id;
            if (found == null) continue;
            collectionDirById[found] = dir;
            if (found == id) return dir;
        }

        collectionDirById.TryRemove(id, out _);
        return null;
    }

    private string? FindTaskFileById(string dir, string taskId)
    {
        if (taskFileById.TryGetValue(taskId, out var remembered)
            && ReadJson<TaskDefinition>(remembered)?.Id == taskId)
        {
            return remembered;
        }

        foreach (var file in TaskFiles(dir))
        {
            var found = ReadJson<TaskDefinition>(file)?.Id;
            if (found == null) continue;
            taskFileById[found] = file;
            if (found == taskId) return file;
        }

        // Not in THIS collection — which is not the same as gone, since the caller may be asking
        // about a task that lives somewhere else. Only a remembered path that pointed into this
        // directory has been disproved.
        if (remembered != null && PathsEqual(Path.GetDirectoryName(remembered), dir))
            taskFileById.TryRemove(taskId, out _);
        return null;
    }

    private Collection? LoadCollectionFromDir(string dir, HashSet<string> seenIds)
    {
        var folderName = Path.GetFileName(dir);
        var file = Path.Combine(dir, ManifestFileName);

        if (!File.Exists(file))
        {
            // A folder of task files someone hand-copied in: give it a manifest so they surface.
            if (!TaskFiles(dir).Any()) return null;
            log.LogWarning("Collection folder {Dir} has no collection.json — recovering", dir);
            var recovered = new Collection
            {
                Name = folderName,
                CreatedUtc = DateTimeOffset.UtcNow,
                ModifiedUtc = DateTimeOffset.UtcNow,
            };
            WriteJson(file, recovered);
            return recovered;
        }

        var collection = ReadJson<Collection>(file);
        if (collection == null)
        {
            log.LogWarning("Skipping unreadable collection file {File}", file);
            return null;
        }
        SchemaMigration.Migrate(collection);

        var changed = false;
        if (seenIds.Contains(collection.Id))
        {
            collection.Id = StoreUtil.NewId(); // Explorer copy-paste duplicate
            changed = true;
        }
        if (!string.Equals(SafeName(collection.Name), folderName, StringComparison.OrdinalIgnoreCase))
        {
            collection.Name = folderName; // renamed in Explorer — the folder wins
            changed = true;
        }
        if (changed) WriteJson(file, collection);
        return collection;
    }

    // Names Windows refuses (device names) plus "collection", which would collide with the
    // manifest file if a task were named that.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "collection",
    };

    /// <summary>
    /// Display name → Windows-safe folder/file name, kept human-readable. LOSSLESS by design:
    /// the JSON keeps the original name verbatim (illegal characters intact), the disk name is
    /// only a sanitized projection of it — parsing back means reading the JSON. The disk-name-
    /// wins healing in LoadTasks/LoadCollectionFromDir therefore compares against SafeName(json
    /// name), so a sanitization difference never counts as a rename and never clobbers the
    /// original.
    /// </summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "Unnamed";
        if (cleaned.Length > 100) cleaned = cleaned[..100].TrimEnd('.', ' ');
        if (ReservedNames.Contains(cleaned)) cleaned = "_" + cleaned;
        return cleaned;
    }

    /// <summary>"Name (2)", "Name (3)", … until the SANITIZED form is free — disk collisions
    /// happen on safe names, so that's the form that must be unique.</summary>
    private static string UniqueBySafeName(string desired, IEnumerable<string?> takenSafeNames)
    {
        var taken = new HashSet<string>(takenSafeNames.Where(n => n != null)!, StringComparer.OrdinalIgnoreCase);
        var name = desired;
        for (var n = 2; taken.Contains(SafeName(name)); n++) name = $"{desired} ({n})";
        return name;
    }

    private static bool PathsEqual(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The store's ONE write path, so version stamping and normalization cannot be
    /// bypassed by any caller — including the hand-edit healing rewrites above.</summary>
    private static void WriteJson<T>(string path, T value)
    {
        SchemaMigration.StampCurrentVersion(value);
        File.WriteAllText(path, JsonSerializer.Serialize(value, AutomataJson.Options));
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AutomataJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }
}

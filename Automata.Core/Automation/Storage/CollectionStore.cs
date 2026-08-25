using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// File-backed store for collections and tasks:
/// <code>
/// &lt;root&gt;\&lt;collectionId&gt;\collection.json
/// &lt;root&gt;\&lt;collectionId&gt;\tasks\&lt;taskId&gt;.json
/// </code>
/// Id-named paths keep renames and duplicate display names safe; the JSON content carries the
/// human-readable names. The store heals drift on load (a task file whose collectionId disagrees
/// with its folder gets the folder's id; a task folder missing collection.json gets a "Recovered"
/// one) so hand-copied files still show up instead of silently vanishing.
/// </summary>
public sealed class CollectionStore
{
    public const string DefaultCollectionName = "Default";

    private readonly ILogger<CollectionStore> log;

    public string RootPath { get; }

    public CollectionStore(string? rootPath = null, ILogger<CollectionStore>? log = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Automata", "collections");
        this.log = log ?? NullLogger<CollectionStore>.Instance;
    }

    // ---- collections -------------------------------------------------------------------------

    public IReadOnlyList<Collection> LoadCollections()
    {
        if (!Directory.Exists(RootPath)) return [];

        var collections = new List<Collection>();
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var collection = LoadCollectionFromDir(dir);
            if (collection != null) collections.Add(collection);
        }
        return collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Collection? GetCollection(string id)
    {
        var dir = Path.Combine(RootPath, id);
        return Directory.Exists(dir) ? LoadCollectionFromDir(dir) : null;
    }

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
        var dir = Path.Combine(RootPath, collection.Id);
        Directory.CreateDirectory(dir);
        WriteJson(Path.Combine(dir, "collection.json"), collection);
    }

    public void DeleteCollection(string id)
    {
        var dir = Path.Combine(RootPath, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
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
        var tasksDir = Path.Combine(RootPath, collectionId, "tasks");
        if (!Directory.Exists(tasksDir)) return [];

        var tasks = new List<TaskDefinition>();
        foreach (var file in Directory.EnumerateFiles(tasksDir, "*.json"))
        {
            var task = ReadJson<TaskDefinition>(file);
            if (task == null)
            {
                log.LogWarning("Skipping unreadable task file {File}", file);
                continue;
            }
            if (task.Id != Path.GetFileNameWithoutExtension(file))
                task.Id = Path.GetFileNameWithoutExtension(file); // file name is authoritative
            if (task.CollectionId != collectionId)
            {
                task.CollectionId = collectionId; // folder is authoritative — heal the file
                WriteJson(file, task);
            }
            tasks.Add(task);
        }

        var order = GetCollection(collectionId)?.TaskOrder ?? [];
        return tasks
            .OrderBy(t => { var i = order.IndexOf(t.Id); return i < 0 ? int.MaxValue : i; })
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public TaskDefinition? GetTask(string taskId)
    {
        if (!Directory.Exists(RootPath)) return null;
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var file = Path.Combine(dir, "tasks", taskId + ".json");
            if (File.Exists(file))
            {
                var task = ReadJson<TaskDefinition>(file);
                if (task != null && task.CollectionId != Path.GetFileName(dir))
                    task.CollectionId = Path.GetFileName(dir);
                return task;
            }
        }
        return null;
    }

    /// <summary>
    /// Persist a task. An empty CollectionId gets the default collection assigned — a task never
    /// exists without a parent collection.
    /// </summary>
    public void SaveTask(TaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.CollectionId))
            task.CollectionId = EnsureDefaultCollection().Id;

        var collection = GetCollection(task.CollectionId)
            ?? throw new InvalidOperationException($"Collection '{task.CollectionId}' not found.");

        if (task.CreatedUtc == default) task.CreatedUtc = DateTimeOffset.UtcNow;
        task.ModifiedUtc = DateTimeOffset.UtcNow;

        var tasksDir = Path.Combine(RootPath, task.CollectionId, "tasks");
        Directory.CreateDirectory(tasksDir);
        WriteJson(Path.Combine(tasksDir, task.Id + ".json"), task);

        if (!collection.TaskOrder.Contains(task.Id))
        {
            collection.TaskOrder.Add(task.Id);
            SaveCollection(collection);
        }
    }

    public void DeleteTask(string taskId)
    {
        var task = GetTask(taskId);
        if (task == null) return;

        File.Delete(Path.Combine(RootPath, task.CollectionId, "tasks", taskId + ".json"));

        var collection = GetCollection(task.CollectionId);
        if (collection != null && collection.TaskOrder.Remove(taskId))
            SaveCollection(collection);
    }

    public TaskDefinition MoveTask(string taskId, string toCollectionId)
    {
        var task = GetTask(taskId)
            ?? throw new InvalidOperationException($"Task '{taskId}' not found.");
        if (task.CollectionId == toCollectionId) return task;
        _ = GetCollection(toCollectionId)
            ?? throw new InvalidOperationException($"Collection '{toCollectionId}' not found.");

        DeleteTask(taskId);
        task.CollectionId = toCollectionId;
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
        StoreUtil.RegenerateStepIds(copy.Steps);
        SaveTask(copy);
        return copy;
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private Collection? LoadCollectionFromDir(string dir)
    {
        var file = Path.Combine(dir, "collection.json");
        var folderId = Path.GetFileName(dir);

        if (!File.Exists(file))
        {
            // A bare task folder someone hand-copied in: give it a manifest so its tasks surface.
            if (!Directory.Exists(Path.Combine(dir, "tasks"))) return null;
            log.LogWarning("Collection folder {Dir} has no collection.json — recovering", dir);
            var recovered = new Collection
            {
                Id = folderId,
                Name = StoreUtil.UniqueName("Recovered", LoadCollectionNamesExcept(folderId)),
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
        if (collection.Id != folderId)
        {
            collection.Id = folderId; // folder is authoritative
            WriteJson(file, collection);
        }
        return collection;
    }

    private IEnumerable<string> LoadCollectionNamesExcept(string excludeId)
    {
        if (!Directory.Exists(RootPath)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            if (Path.GetFileName(dir) == excludeId) continue;
            var file = Path.Combine(dir, "collection.json");
            if (!File.Exists(file)) continue;
            var name = ReadJson<Collection>(file)?.Name;
            if (name != null) yield return name;
        }
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, AutomataJson.Options));

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), AutomataJson.Options); }
        catch (JsonException) { return null; }
    }
}

using Automata.Core.Automation.Data;
using Automata.Core.Automation.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Collections and their tasks, in the <c>Collections</c> and <c>Tasks</c> tables of the Automata
/// database (see <see cref="AutomataDatabase"/>).
/// <para>
/// Ids are the identity; names are for people, and are kept unique where a person or the runner's
/// <c>--task &lt;name&gt;</c> would otherwise be left guessing: collection names across the
/// workspace, task names within their collection. A rename onto a taken name keeps both, suffixing
/// the one being saved " (2)".
/// </para>
/// <para>
/// <b>Deleting only hides</b> (HOUSE-LAW-2): a deleted collection or task keeps its row with
/// <c>DeletedUtc</c> set and stops appearing anywhere. Saving a task under a hidden id brings it
/// back — which is how the demo generator restores an example somebody deleted.
/// </para>
/// </summary>
public sealed class CollectionStore
{
    public const string DefaultCollectionName = "Default";

    private readonly ILogger<CollectionStore> log;

    /// <summary>
    /// Serialises writes in this process. A save is a read-modify-write across two rows — the task
    /// and its collection's TaskOrder — and the app can be saving an edit while a run on another
    /// thread saves a healed task. Across processes each save is one database transaction.
    /// </summary>
    private readonly Lock saveGate = new();

    public CollectionStore(AutomataDatabase database, ILogger<CollectionStore>? log = null)
    {
        Database = database;
        this.log = log ?? NullLogger<CollectionStore>.Instance;
    }

    public AutomataDatabase Database { get; }

    // ---- collections -------------------------------------------------------------------------

    public IReadOnlyList<Collection> LoadCollections()
    {
        using var db = Database.CreateDbContext();
        return db.Collections.AsNoTracking().ToList()
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Collection? GetCollection(string id)
    {
        using var db = Database.CreateDbContext();
        return db.Collections.AsNoTracking().FirstOrDefault(c => c.Id == id);
    }

    public Collection CreateCollection(string name)
    {
        var collection = new Collection
        {
            Name = StoreUtil.UniqueName(name, LoadCollections().Select(c => c.Name)),
            CreatedUtc = StoreUtil.UtcNow(),
            ModifiedUtc = StoreUtil.UtcNow(),
        };
        SaveCollection(collection);
        return collection;
    }

    public void SaveCollection(Collection collection)
    {
        lock (saveGate)
        {
            using var db = Database.CreateDbContext();
            using var tx = db.Database.BeginTransaction();
            SaveCollectionCore(db, collection);
            db.SaveChanges();
            tx.Commit();
        }
    }

    private static void SaveCollectionCore(AutomataDb db, Collection collection)
    {
        collection.ModifiedUtc = StoreUtil.UtcNow();
        if (collection.CreatedUtc == default) collection.CreatedUtc = collection.ModifiedUtc;

        // Renaming onto another collection's name: keep both, suffix this one.
        var otherNames = db.Collections.AsNoTracking()
            .Where(c => c.Id != collection.Id)
            .Select(c => c.Name)
            .ToList();
        collection.Name = StoreUtil.UniqueName(collection.Name, otherNames);

        SchemaMigration.StampCurrentVersion(collection);
        Upsert(db, collection, collection.Id);
    }

    /// <summary>Hides the collection and every task in it. Nothing is erased.</summary>
    public void DeleteCollection(string id)
    {
        lock (saveGate)
        {
            using var db = Database.CreateDbContext();
            var collection = db.Collections.FirstOrDefault(c => c.Id == id);
            if (collection == null) return;
            var now = StoreUtil.UtcNow();
            db.Entry(collection).Property(AutomataDb.DeletedUtc).CurrentValue = now;
            foreach (var task in db.Tasks.Where(t => t.CollectionId == id))
                db.Entry(task).Property(AutomataDb.DeletedUtc).CurrentValue = now;
            db.SaveChanges();
        }
    }

    public Collection DuplicateCollection(string id)
    {
        var source = GetCollection(id)
            ?? throw new InvalidOperationException($"Collection '{id}' not found.");
        var tasks = LoadTasks(id);

        var copy = StoreUtil.Clone(source);
        copy.Id = StoreUtil.NewId();
        copy.Name = StoreUtil.UniqueName(source.Name, LoadCollections().Select(c => c.Name));
        copy.CreatedUtc = StoreUtil.UtcNow();
        copy.TaskOrder = [];
        SaveCollection(copy);

        // Every task's new id is minted BEFORE any of them is written, because the copies have to
        // be wired to each other: a runTask step or an input wired to an earlier task's output must
        // name the copy, not reach back into the collection this was duplicated from. That map
        // cannot be built one task at a time — task 1 is referenced by task 2, which has not been
        // cloned yet, and task 2 by task 1.
        var copies = tasks.Select(StoreUtil.Clone).ToList();
        var taskIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, taskCopy) in tasks.Zip(copies))
        {
            taskCopy.Id = StoreUtil.NewId();
            taskIds[original.Id] = taskCopy.Id;
        }

        foreach (var taskCopy in copies)
        {
            taskCopy.CollectionId = copy.Id;
            StoreUtil.ReidentifySteps(taskCopy);
            StoreUtil.RemapTaskIds(taskCopy, taskIds);
            SaveTask(taskCopy);
        }
        return GetCollection(copy.Id)!;
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
            CreatedUtc = StoreUtil.UtcNow(),
            ModifiedUtc = StoreUtil.UtcNow(),
        };
        SaveCollection(collection);
        return collection;
    }

    // ---- tasks -------------------------------------------------------------------------------

    /// <summary>Tasks of one collection, ordered by its TaskOrder; unlisted tasks sort last by name.</summary>
    public IReadOnlyList<TaskDefinition> LoadTasks(string collectionId)
    {
        using var db = Database.CreateDbContext();
        var collection = db.Collections.AsNoTracking().FirstOrDefault(c => c.Id == collectionId);
        if (collection == null) return [];
        var tasks = db.Tasks.AsNoTracking().Where(t => t.CollectionId == collectionId).ToList();
        return Ordered(tasks, collection.TaskOrder);
    }

    /// <summary>Every visible task in the workspace, in no particular order.</summary>
    public IReadOnlyList<TaskDefinition> LoadAllTasks()
    {
        using var db = Database.CreateDbContext();
        return db.Tasks.AsNoTracking().ToList();
    }

    public TaskDefinition? GetTask(string taskId)
    {
        using var db = Database.CreateDbContext();
        return db.Tasks.AsNoTracking().FirstOrDefault(t => t.Id == taskId);
    }

    /// <summary>Whether any row — visible or deleted — already holds this task id. Imports ask
    /// this, because a hidden row still owns its key.</summary>
    public bool TaskIdTaken(string taskId)
    {
        using var db = Database.CreateDbContext();
        return db.Tasks.IgnoreQueryFilters().Any(t => t.Id == taskId);
    }

    /// <summary>Whether any row — visible or deleted — already holds this collection id.</summary>
    public bool CollectionIdTaken(string collectionId)
    {
        using var db = Database.CreateDbContext();
        return db.Collections.IgnoreQueryFilters().Any(c => c.Id == collectionId);
    }

    /// <summary>
    /// Persist a task. An empty CollectionId gets the default collection assigned — a task never
    /// exists without a parent collection. A task whose CollectionId changed leaves its old
    /// collection's order and joins the new one's.
    /// </summary>
    public void SaveTask(TaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.CollectionId))
            task.CollectionId = EnsureDefaultCollection().Id;

        lock (saveGate)
        {
            using var db = Database.CreateDbContext();
            using var tx = db.Database.BeginTransaction();

            var collection = db.Collections.FirstOrDefault(c => c.Id == task.CollectionId)
                ?? throw new InvalidOperationException($"Collection '{task.CollectionId}' not found.");

            if (task.CreatedUtc == default) task.CreatedUtc = StoreUtil.UtcNow();
            task.ModifiedUtc = StoreUtil.UtcNow();

            // Renaming onto a sibling task's name: keep both, suffix this one.
            var siblingNames = db.Tasks.AsNoTracking()
                .Where(t => t.CollectionId == task.CollectionId && t.Id != task.Id)
                .Select(t => t.Name)
                .ToList();
            task.Name = StoreUtil.UniqueName(task.Name, siblingNames);

            var previousCollectionId = db.Tasks.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == task.Id)
                .Select(t => t.CollectionId)
                .FirstOrDefault();
            if (previousCollectionId != null && previousCollectionId != task.CollectionId)
            {
                var previous = db.Collections.IgnoreQueryFilters().FirstOrDefault(c => c.Id == previousCollectionId);
                if (previous != null && previous.TaskOrder.Remove(task.Id))
                    db.Entry(previous).Property(c => c.TaskOrder).IsModified = true;
            }

            SchemaMigration.StampCurrentVersion(task);
            Upsert(db, task, task.Id);

            if (!collection.TaskOrder.Contains(task.Id))
            {
                collection.TaskOrder.Add(task.Id);
                collection.ModifiedUtc = StoreUtil.UtcNow();
                db.Entry(collection).Property(c => c.TaskOrder).IsModified = true;
            }

            db.SaveChanges();
            tx.Commit();
        }
    }

    /// <summary>Hides a task and takes it out of its collection's order. Nothing is erased.</summary>
    public void DeleteTask(string taskId)
    {
        lock (saveGate)
        {
            using var db = Database.CreateDbContext();
            var task = db.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null) return;
            db.Entry(task).Property(AutomataDb.DeletedUtc).CurrentValue = StoreUtil.UtcNow();

            var collection = db.Collections.IgnoreQueryFilters().FirstOrDefault(c => c.Id == task.CollectionId);
            if (collection != null && collection.TaskOrder.Remove(taskId))
                db.Entry(collection).Property(c => c.TaskOrder).IsModified = true;
            db.SaveChanges();
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

        if (task.Demo != null)
        {
            DeleteTask(taskId);
            task.Demo = null;
            task.Id = StoreUtil.NewId();
        }
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
        copy.CreatedUtc = StoreUtil.UtcNow();
        // A copy of an example is not the example. Two tasks answering to one demo key would leave
        // the generator restoring whichever it found first and silently leaving the other behind.
        copy.Demo = null;
        StoreUtil.ReidentifySteps(copy);
        // The copy's own task id changed too, and a binding may name it outright rather than
        // leaving it null for "this task" — so the one entry that map needs is this one.
        StoreUtil.RemapTaskIds(copy, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [source.Id] = copy.Id,
        });
        SaveTask(copy);
        return copy;
    }

    // ---- bulk import -------------------------------------------------------------------------

    /// <summary>
    /// Writes a collection and its tasks exactly as given — ids, names, timestamps and order — in
    /// one transaction. For the one-time import of the old JSON files, where the caller has
    /// already made ids unique and the history (created/modified) is worth keeping.
    /// </summary>
    internal void InsertVerbatim(Collection collection, IReadOnlyList<TaskDefinition> tasks)
    {
        lock (saveGate)
        {
            using var db = Database.CreateDbContext();
            using var tx = db.Database.BeginTransaction();
            SchemaMigration.StampCurrentVersion(collection);
            db.Collections.Add(collection);
            foreach (var task in tasks)
            {
                task.CollectionId = collection.Id;
                SchemaMigration.StampCurrentVersion(task);
                db.Tasks.Add(task);
            }
            db.SaveChanges();
            tx.Commit();
        }
        log.LogInformation("Imported collection '{Name}' with {Count} task(s)", collection.Name, tasks.Count);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    internal static IReadOnlyList<TaskDefinition> Ordered(IEnumerable<TaskDefinition> tasks, List<string> order) =>
        tasks
            .OrderBy(t => { var i = order.IndexOf(t.Id); return i < 0 ? int.MaxValue : i; })
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Insert or overwrite by id, whether the row is visible or hidden — a save of a hidden id
    /// brings it back. The entity is attached whole, so every column (complex and JSON ones
    /// included) is written from it.
    /// </summary>
    private static void Upsert<T>(AutomataDb db, T entity, string id) where T : class
    {
        var exists = typeof(T) == typeof(TaskDefinition)
            ? db.Tasks.IgnoreQueryFilters().Any(t => t.Id == id)
            : db.Collections.IgnoreQueryFilters().Any(c => c.Id == id);

        // A tracked instance with the same key (e.g. the collection loaded to update its order)
        // would make attaching this one throw; hand its values over instead.
        var tracked = db.ChangeTracker.Entries<T>()
            .FirstOrDefault(e => Equals(e.Property("Id").CurrentValue, id));
        if (tracked != null && !ReferenceEquals(tracked.Entity, entity))
            tracked.State = EntityState.Detached;

        var entry = db.Entry(entity);
        entry.State = exists ? EntityState.Modified : EntityState.Added;
        entry.Property(AutomataDb.DeletedUtc).CurrentValue = null;
        if (exists) entry.Property(AutomataDb.DeletedUtc).IsModified = true;
    }
}

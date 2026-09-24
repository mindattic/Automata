using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Versioning for the JSON shape of collections and tasks — the shape export files, the old
/// per-task files and the database's step-tree column all share. (The database's own schema is
/// versioned separately, by EF migrations.)
/// <para>
/// v1 → v2 added scoped engine settings (<see cref="EngineSettingsOverride"/>) to collections,
/// tasks and steps. The change is purely additive: System.Text.Json leaves a missing property at
/// its default, so every v1 document already loads correctly and there is nothing to rewrite.
/// </para>
/// <para>
/// <see cref="Migrate(TaskDefinition)"/> runs on everything that comes IN — an import, the
/// one-time move off the old files — and <see cref="StampCurrentVersion{T}"/> on everything the
/// store writes, so a row always carries the version whose shape it was written in.
/// </para>
/// </summary>
public static class SchemaMigration
{
    public const int CurrentCollectionVersion = 2;
    public const int CurrentTaskVersion = 2;

    /// <summary>Bump when the export envelope itself changes shape, not when the model grows.</summary>
    public const int CurrentExportVersion = 2;

    /// <summary>
    /// Brings a just-loaded collection into the current in-memory shape. Does not write.
    /// </summary>
    public static Collection Migrate(Collection collection)
    {
        Normalize(collection);
        return collection;
    }

    /// <summary>
    /// Brings a just-loaded task into the current in-memory shape. Does not write.
    /// </summary>
    public static TaskDefinition Migrate(TaskDefinition task)
    {
        Normalize(task);
        return task;
    }

    /// <summary>
    /// Called from the store's write paths, so every persisted entity — saved by the user,
    /// imported, or moved off the old files — lands stamped with the version whose shape it was
    /// actually written in.
    /// </summary>
    public static void StampCurrentVersion<T>(T value)
    {
        switch (value)
        {
            case Collection collection:
                Normalize(collection);
                collection.SchemaVersion = CurrentCollectionVersion;
                break;
            case TaskDefinition task:
                Normalize(task);
                task.SchemaVersion = CurrentTaskVersion;
                break;
        }
    }

    private static void Normalize(Collection collection) =>
        collection.Settings = Prune(collection.Settings);

    private static void Normalize(TaskDefinition task)
    {
        task.Settings = Prune(task.Settings);
        NormalizeSteps(task.Steps);
    }

    private static void NormalizeSteps(List<Step>? steps)
    {
        foreach (var step in steps ?? [])
        {
            step.Settings = Prune(step.Settings);
            NormalizeSteps(step.Children);
        }
    }

    /// <summary>
    /// An override that overrides nothing is noise: it bloats an export and, worse, makes a task
    /// that has never been configured look configured. Drop it.
    /// </summary>
    private static EngineSettingsOverride? Prune(EngineSettingsOverride? settings) =>
        settings is null || settings.IsEmpty ? null : settings;
}

using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Schema versioning for the on-disk model.
/// <para>
/// v1 → v2 added scoped engine settings (<see cref="EngineSettingsOverride"/>) to collections,
/// tasks and steps. The change is purely additive: System.Text.Json leaves a missing property at
/// its default, so every v1 file already loads correctly and there is nothing to rewrite.
/// </para>
/// <para>
/// That is why migration here is <b>lazy</b>: files are stamped with the current version when
/// they are written for some other reason, never rewritten en masse on first launch. Opening v3
/// against an existing Documents\Automata therefore touches nothing until the user edits
/// something — which is also what keeps the first-run experience byte-identical.
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
    /// Called from the store's single write path, so every persisted entity — whether saved by
    /// the user or rewritten by the store's own hand-edit healing — lands stamped with the
    /// version whose shape it was actually written in.
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
    /// An override that overrides nothing is noise: it bloats the file and, worse, makes a task
    /// that has never been configured look configured. Drop it.
    /// </summary>
    private static EngineSettingsOverride? Prune(EngineSettingsOverride? settings) =>
        settings is null || settings.IsEmpty ? null : settings;
}

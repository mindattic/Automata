namespace Automata.Core.Automation.Model;

/// <summary>
/// A named group of tasks. On disk: one folder per collection
/// (<c>collections\&lt;id&gt;\collection.json</c> + <c>tasks\&lt;taskId&gt;.json</c>).
/// </summary>
public sealed class Collection
{
    /// <summary>Stamped on write by SchemaMigration; see that type for the migration policy.</summary>
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>
    /// Explicit task display order (task ids). Tasks not listed here sort last, by name —
    /// tolerated so a hand-dropped task file still shows up.
    /// </summary>
    public List<string> TaskOrder { get; set; } = [];

    /// <summary>
    /// Engine settings overridden at this scope; null (the usual case) means "inherit everything".
    /// Resolved through global -> collection -> task -> step by EngineSettingsResolver.
    /// </summary>
    public EngineSettingsOverride? Settings { get; set; }
}

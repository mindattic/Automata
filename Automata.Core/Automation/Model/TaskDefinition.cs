namespace Automata.Core.Automation.Model;

/// <summary>
/// A replayable browser task: an ordered tree of steps. Named TaskDefinition (not Task) to stay
/// clear of System.Threading.Tasks.Task. Fully self-contained in one JSON file so a single task
/// can be shared by copying that file.
/// </summary>
public sealed class TaskDefinition
{
    /// <summary>Stamped on write by SchemaMigration; see that type for the migration policy.</summary>
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string CollectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Optional convenience: navigated to before the first step when set.</summary>
    public string? StartUrl { get; set; }

    /// <summary>
    /// Values this task takes from whoever runs it. Empty for the ordinary case; a task with
    /// inputs is one written to be run more than one way.
    /// </summary>
    public List<TaskInput> Inputs { get; set; } = [];

    /// <summary>
    /// Values this task publishes when it finishes, for later tasks in the same collection to
    /// bind their inputs to. Empty for the ordinary case; a task with outputs is one written to
    /// hand something on.
    /// </summary>
    public List<TaskOutput> Outputs { get; set; } = [];

    public List<Step> Steps { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>
    /// Engine settings overridden at this scope; null (the usual case) means "inherit everything".
    /// Resolved through global -> collection -> task -> step by EngineSettingsResolver.
    /// </summary>
    public EngineSettingsOverride? Settings { get; set; }

    /// <summary>
    /// Set on tasks the demo generator produced; null on everything a person made. Lets the
    /// generator tell an untouched demo from one the user has since edited, so regenerating can
    /// refresh the first silently and ask about the second.
    /// </summary>
    public DemoOrigin? Demo { get; set; }
}

/// <summary>
/// Marks a task as a generated demo, and records what the factory version looked like when it was
/// written.
/// <para>
/// The stored hash is what makes the difference between "this demo is out of date" and "this demo
/// has been changed by hand" answerable. Without it, regenerating could only either clobber
/// everything or ask about everything, and both are wrong: nobody wants to be asked about a demo
/// they have never opened.
/// </para>
/// </summary>
public sealed class DemoOrigin
{
    /// <summary>Stable identity of the factory demo this came from, e.g. "shop-prices-sequential".</summary>
    public string Key { get; set; } = "";

    /// <summary>Hash of the factory steps as generated. Compared against the task's current
    /// content to detect a hand edit, and against the current factory to detect staleness.</summary>
    public string FactoryHash { get; set; } = "";
}

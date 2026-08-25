namespace Automata.Core.Automation.Model;

/// <summary>
/// A replayable browser task: an ordered tree of steps. Named TaskDefinition (not Task) to stay
/// clear of System.Threading.Tasks.Task. Fully self-contained in one JSON file so a single task
/// can be shared by copying that file.
/// </summary>
public sealed class TaskDefinition
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string CollectionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Optional convenience: navigated to before the first step when set.</summary>
    public string? StartUrl { get; set; }

    public List<Step> Steps { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
}

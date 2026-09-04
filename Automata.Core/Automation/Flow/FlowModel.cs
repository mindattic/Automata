using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Flow;

/// <summary>How much a diagnostic matters.</summary>
public enum FlowSeverity
{
    /// <summary>Compiled, but something was ignored or assumed.</summary>
    Warning,

    /// <summary>Could not be compiled.</summary>
    Error,
}

/// <summary>
/// One problem with a feature file, anchored to where it is. Line and column are what make the
/// authoring loop mechanical: the user sees exactly which line is wrong, and the LLM gets the same
/// text back to repair rather than being told "that didn't work".
/// </summary>
public sealed record FlowDiagnostic(FlowSeverity Severity, int Line, int Column, string Message)
{
    public override string ToString() => $"{Severity} at line {Line}:{Column} — {Message}";
}

/// <summary>A dataset the compiler produced from an inline Examples table. The caller writes it;
/// the compiler stays pure.</summary>
public sealed record InlineDataset(string Name, IReadOnlyList<Dictionary<string, string>> Rows);

/// <summary>
/// Everything a feature file compiles to. <see cref="Collection"/> is null when compilation
/// failed; check <see cref="HasErrors"/> rather than the shape.
/// </summary>
public sealed record FlowCompileResult(
    Collection? Collection,
    IReadOnlyList<TaskDefinition> Tasks,
    IReadOnlyList<InlineDataset> Datasets,
    IReadOnlyList<FlowDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == FlowSeverity.Error);

    public static FlowCompileResult Failed(params FlowDiagnostic[] diagnostics) =>
        new(null, [], [], diagnostics);
}

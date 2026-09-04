namespace Automata.Core.Automation.Model;

/// <summary>Where a bound value comes from.</summary>
public enum BindingKind
{
    /// <summary>A fixed string. Equivalent to just setting the field, but lets a binding be
    /// switched back to a literal without losing the shape.</summary>
    Literal,

    /// <summary>An output published by an earlier step (<c>extract text … as price</c>).</summary>
    StepOutput,

    /// <summary>A named parameter supplied to the task by its caller.</summary>
    TaskInput,

    /// <summary>One column of the row currently being iterated by an enclosing ForEach.</summary>
    DatasetColumn,

    /// <summary>The whole current row, as JSON.</summary>
    DatasetRow,

    /// <summary>An environment variable — the sanctioned way to keep a secret out of the store.</summary>
    EnvVar,
}

/// <summary>
/// A picker-built reference to a value. Never hand-typed: the editor enumerates the valid sources
/// and writes one of these, which is what keeps the set of expressible bindings closed and
/// statically checkable.
/// <para>
/// <see cref="Prefix"/> and <see cref="Suffix"/> are the ONE templating affordance —
/// <c>"https://shop.example/item/" + row.sku</c> — deliberately capped at a literal either side of
/// a single reference. Anything needing real composition belongs in the Gherkin authoring layer,
/// not smuggled into a dropdown.
/// </para>
/// </summary>
public sealed class BindingRef
{
    public BindingKind Kind { get; set; } = BindingKind.Literal;

    /// <summary>Used when <see cref="Kind"/> is <see cref="BindingKind.Literal"/>.</summary>
    public string? Literal { get; set; }

    public string? Prefix { get; set; }
    public string? Suffix { get; set; }

    /// <summary>StepOutput: the step that published it. Null means "this task", resolved by id.</summary>
    public string? SourceTaskId { get; set; }

    public string? SourceStepId { get; set; }

    /// <summary>StepOutput: which of the source step's declared outputs.</summary>
    public string? OutputField { get; set; }

    /// <summary>TaskInput: the parameter name.</summary>
    public string? ParameterName { get; set; }

    /// <summary>DatasetColumn / DatasetRow.</summary>
    public string? DatasetName { get; set; }

    public string? ColumnName { get; set; }

    /// <summary>EnvVar: the variable name.</summary>
    public string? EnvVarName { get; set; }

    /// <summary>Short human description for the editor chip, e.g. "Extract total → text".</summary>
    public string? Label { get; set; }
}

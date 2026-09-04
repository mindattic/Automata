namespace Automata.Core.Automation.Model;

/// <summary>
/// A value a step publishes for later steps to bind to. Declared at design time — not discovered
/// at run time — so the binding picker can enumerate every valid source without executing
/// anything, which is what makes the whole picker-driven data flow possible.
/// </summary>
public sealed class OutputField
{
    /// <summary>Referenced by <see cref="BindingRef.OutputField"/>; unique within its step.</summary>
    public string Name { get; set; } = "";

    /// <summary>"string" today. Reserved for later typing of dataset columns.</summary>
    public string Type { get; set; } = "string";

    public string? Description { get; set; }
}

/// <summary>
/// A value a task takes FROM its caller — the same search run for a different term, the same
/// invoice run for a different month.
/// <para>
/// Declared on the task, exactly like an output is declared on a step, and for the same reason:
/// the binding picker can then offer it everywhere inside the task without executing anything.
/// </para>
/// <para>
/// This is the answer to templating, and it is deliberately NOT <c>{{query}}</c> in a text field.
/// A hand-typed placeholder is an expression language arriving one string at a time — nothing can
/// enumerate it, nothing can check it, and a typo in it fails at run time as a value that silently
/// stayed literal. A declared input is a record the editor can list, the picker can offer, and the
/// engine can refuse by name when nothing supplies it.
/// </para>
/// </summary>
public sealed class TaskInput
{
    /// <summary>Referenced by <see cref="BindingRef.ParameterName"/>; unique within its task.</summary>
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>
    /// What to use when the caller supplies nothing. Null means the input is REQUIRED, and a run
    /// that does not supply it fails at the step that needed it, naming it — which is the whole
    /// point of declaring inputs rather than interpolating strings.
    /// </summary>
    public string? Default { get; set; }
}

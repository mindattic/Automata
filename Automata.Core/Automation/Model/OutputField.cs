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

    /// <summary>
    /// Where this input's value comes from when the task runs as part of a collection: an earlier
    /// task in that collection, and one of the outputs that task declares.
    /// <para>
    /// Null for the ordinary case, and it is only ever a hint — a value supplied directly (the
    /// CLI's <c>--input</c>, a <c>runTask</c> step's binding) still wins, and a wiring whose task
    /// has not run in this collection falls back to <see cref="Default"/> exactly as if nothing
    /// had been wired. That keeps a task runnable on its own, which is what makes it worth
    /// wiring into a collection in the first place.
    /// </para>
    /// </summary>
    public TaskOutputRef? From { get; set; }
}

/// <summary>Points at one output of one task — picked from a list, never typed.</summary>
public sealed class TaskOutputRef
{
    public string TaskId { get; set; } = "";

    /// <summary>The task's name as it was when the wiring was made. Only ever used to explain a
    /// wiring whose task has since been deleted; the id is what resolves it.</summary>
    public string? TaskName { get; set; }

    /// <summary>Names a <see cref="TaskOutput"/> of that task.</summary>
    public string OutputName { get; set; } = "";
}

/// <summary>
/// A value a task PUBLISHES when it finishes — the counterpart to <see cref="TaskInput"/>, and
/// what makes a collection a pipeline rather than a list of unrelated jobs.
/// <para>
/// It names a step inside the task and one of that step's declared outputs, so publishing is a
/// selection rather than a second copy of the value. A later task in the same collection binds its
/// own input to this by name, which means task 2 depends on task 1's CONTRACT and not on task 1's
/// internals: renaming a step, or re-recording it, cannot silently break the task downstream.
/// </para>
/// </summary>
public sealed class TaskOutput
{
    /// <summary>Unique within its task; what a later task's input is wired to.</summary>
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The <see cref="Step.Id"/> inside this task that produces the value.</summary>
    public string SourceStepId { get; set; } = "";

    /// <summary>
    /// Which of that step's declared outputs to publish. Null means the step's first one, which is
    /// what a step with a single output (the common case) always means.
    /// </summary>
    public string? SourceOutputField { get; set; }
}

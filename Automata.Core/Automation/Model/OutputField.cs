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

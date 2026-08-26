namespace Automata.Core.Automation.Model;

public enum StepAction
{
    /// <summary>Navigate the target pane to <see cref="Step.Url"/>.</summary>
    Navigate,

    /// <summary>Click the target element (button, link, or any element).</summary>
    Click,

    /// <summary>
    /// Type <see cref="Step.Value"/> into the target with real CDP keystrokes — for fields whose
    /// page logic reacts to genuine keydown/keypress events.
    /// </summary>
    TypeText,

    /// <summary>
    /// Set the target's value directly (native property setter + input/change events) — faster
    /// than TypeText and works for React-controlled inputs.
    /// </summary>
    SetValue,

    /// <summary>
    /// Press the Enter key (real CDP key event). With a <see cref="Step.Target"/>, the element
    /// is focused first; without one, Enter goes to whatever currently has focus — the way to
    /// submit search boxes and Enter-to-submit forms where clicking a button is unreliable.
    /// </summary>
    PressEnter,

    /// <summary>Ensure the target checkbox ends up checked.</summary>
    Check,

    /// <summary>Ensure the target checkbox ends up unchecked.</summary>
    Uncheck,

    /// <summary>Select the target radio input (or role=radio widget).</summary>
    SelectRadio,

    /// <summary>Select the option with visible text <see cref="Step.Value"/> in the target select/combobox.</summary>
    SelectOption,

    /// <summary>Attach the local file at <see cref="Step.Value"/> to the target file input (no native picker).</summary>
    UploadFile,

    /// <summary>Wait until the target resolves and is visible (within the step timeout).</summary>
    WaitForElement,

    /// <summary>Fail the run unless the target exists and (when <see cref="Step.Value"/> is set) its text/value matches.</summary>
    AssertElement,

    /// <summary>Read the target's text content into the run output/log.</summary>
    ExtractText,

    /// <summary>No action of its own — a pure container for <see cref="Step.Children"/>.</summary>
    Group,
}

/// <summary>
/// One step in a task. A step performs its own action (if any), auto-confirms its post-condition
/// (value read back, checked state, navigation settled, …), then runs <see cref="Children"/>
/// sequentially under the same rule.
/// </summary>
public sealed class Step
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public StepAction Action { get; set; }

    /// <summary>Human title shown in the sidebar tree, e.g. "Click 'Sign in'".</summary>
    public string Label { get; set; } = "";

    /// <summary>Element target; null for Navigate and Group.</summary>
    public ElementFingerprint? Target { get; set; }

    /// <summary>Text to type/set, option label, local file path, or expected text — per action.</summary>
    public string? Value { get; set; }

    /// <summary>Navigate only.</summary>
    public string? Url { get; set; }

    /// <summary>Halt replay before this step until the user clicks Continue in the sidebar.</summary>
    public bool PauseForUser { get; set; }

    /// <summary>
    /// INFORMATIONAL marker (◆ in the tree) for a step that commits a permanent write
    /// (submit/save/purchase…). Auto-flagged at record time for submit-ish clicks;
    /// user-toggleable. The replay engine does NOT gate on it — Run executes every step
    /// (the old Dry Run mode that stopped here was removed by design).
    /// </summary>
    public bool IsCommitPoint { get; set; }

    /// <summary>Resolve/settle budget override in milliseconds; null = engine default (10s).</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Substeps, executed sequentially after this step's own action confirms.</summary>
    public List<Step> Children { get; set; } = [];
}

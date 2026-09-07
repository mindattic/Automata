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

    // ---- flow control (v3) --------------------------------------------------------------------
    // Appended, never reordered. The enum serializes as camelCase STRINGS so member order is not
    // part of the on-disk contract, but keeping additions at the end still keeps diffs honest.

    /// <summary>Pause — for a duration, until a time of day, until a condition, or for a signal.
    /// See <see cref="Step.Wait"/>.</summary>
    Wait,

    /// <summary>Run <see cref="Step.Children"/> once per row of a dataset.</summary>
    ForEach,

    /// <summary>Run <see cref="Step.Children"/> only when <see cref="Step.Condition"/> holds.</summary>
    If,

    /// <summary>
    /// Run <see cref="Step.Children"/> only when the <see cref="If"/> immediately before it did
    /// NOT.
    /// <para>
    /// A sibling rather than a second child list on the <c>If</c>, for two reasons. It is how a
    /// person sketches it — "if this, do that, otherwise do the other" is three things in a row,
    /// not one thing with two insides — and it is what the sidebar's tree, its drag-and-drop and
    /// its insert gaps already know how to render, because it is an ordinary step with ordinary
    /// children. The cost is that an else can be dragged away from its if; the engine says so by
    /// name rather than guessing what was meant.
    /// </para>
    /// </summary>
    Else,

    /// <summary>Invoke another task inline.</summary>
    RunTask,

    /// <summary>Write bound values as a row of a named dataset.</summary>
    WriteDataset,

    /// <summary>
    /// Read many rows off the current page and write them to a dataset, so a later
    /// <see cref="ForEach"/> can iterate something gathered while browsing rather than only a file
    /// a human supplied. See <see cref="Step.Harvest"/>.
    /// </summary>
    ExtractAll,

    /// <summary>
    /// Zoom the page, so a later step can reach something a cramped layout was hiding. See
    /// <see cref="Step.ZoomPercent"/>.
    /// </summary>
    SetZoom,

    /// <summary>
    /// Reduce one column of a dataset to a single number — a total, a count, the smallest, the
    /// largest, the average. See <see cref="Step.Aggregate"/>.
    /// </summary>
    Aggregate,

    /// <summary>
    /// Look for <see cref="Step.Target"/> right now and say whether it is there — unlike
    /// <see cref="WaitForElement"/> and <see cref="AssertElement"/>, absence is not a failure, it
    /// is the answer. Publishes <c>"true"</c> or <c>"false"</c> as this step's declared output, so
    /// an <see cref="If"/> straight after can branch on it (<c>isTrue</c> / <c>isFalse</c>) —
    /// "did this search return anything, or should this row be skipped" — without a page that
    /// legitimately has nothing to find aborting the whole run.
    /// </summary>
    CheckElement,
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

    /// <summary>
    /// Every step in a tree, parents before their children. One walk, shared by everything that
    /// needs to see a whole task rather than its top level — re-keying ids on import, attributing
    /// a self-heal to the task it happened in, counting what an example demonstrates.
    /// </summary>
    public static IEnumerable<Step> Flatten(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Flatten(step.Children)) yield return child;
        }
    }

    /// <summary>
    /// Values this step publishes for later steps to bind to — an ExtractText step's captured
    /// text, for instance. Declared here at design time rather than discovered at run time, which
    /// is what lets the binding picker enumerate every valid source without executing anything.
    /// </summary>
    public List<OutputField>? Outputs { get; set; }

    /// <summary>
    /// Picker-built overrides keyed by field name ("Value", "Url"). A field with an entry here
    /// resolves its binding at run time and ignores the literal beside it — the literal is kept
    /// rather than cleared, so unbinding restores what was there before.
    /// </summary>
    public Dictionary<string, BindingRef>? Bindings { get; set; }

    /// <summary>
    /// Redact this step's value and extracted text before they reach any log, run artifact or the
    /// sidebar. Mirrors the recorder's existing password-masking convention.
    /// </summary>
    public bool Masked { get; set; }

    /// <summary><see cref="StepAction.Wait"/>.</summary>
    public WaitSpec? Wait { get; set; }

    /// <summary><see cref="StepAction.ForEach"/>.</summary>
    public ForEachSpec? ForEach { get; set; }

    /// <summary><see cref="StepAction.If"/>.</summary>
    public ConditionSpec? Condition { get; set; }

    /// <summary>
    /// <see cref="StepAction.Else"/>: the id of the <see cref="StepAction.If"/> this branch is the
    /// other half of.
    /// <para>
    /// The pairing is otherwise pure ADJACENCY — whatever `if` happens to sit immediately before —
    /// and adjacency cannot tell "correctly paired" from "accidentally next to a different `if`".
    /// Deleting an `if` that had another one before it silently hands its `otherwise` to that other
    /// one: the task still runs, still reports success, and takes the wrong branch. An id makes that
    /// case loud.
    /// </para>
    /// <para>
    /// Null on a step written before this existed, and on one a person moved deliberately; the
    /// engine falls back to the adjacency check then, so nothing already on disk breaks.
    /// </para>
    /// </summary>
    public string? PairedIfId { get; set; }

    /// <summary><see cref="StepAction.RunTask"/>: the task to invoke.</summary>
    public string? RunTaskId { get; set; }

    /// <summary>
    /// <see cref="StepAction.RunTask"/>: what to pass for the called task's declared inputs, keyed
    /// by input name. Resolved in the CALLER's scope, so one task can hand another a value it read
    /// off a page or took from its own inputs. Anything not named here falls back to that input's
    /// default.
    /// </summary>
    public Dictionary<string, BindingRef>? RunTaskInputs { get; set; }

    /// <summary>
    /// <see cref="StepAction.RunTask"/>: open the called task's own start URL before running it.
    /// <para>
    /// False is the default and the rule this app has always had — a called task begins on
    /// whatever page the caller left open, which is what makes a subtask reusable in more than one
    /// context. The trouble was that the rule was invisible: nothing in the editor said it, and the
    /// only place it was written down was one example's description.
    /// </para>
    /// <para>
    /// So it is a field rather than a paragraph. Both behaviours are now something a step SAYS,
    /// which is the same reason a condition is a record and not an expression — you can read a task
    /// and know what it will do without having learnt a convention first.
    /// </para>
    /// </summary>
    public bool RunTaskOpensStartUrl { get; set; }

    /// <summary><see cref="StepAction.WriteDataset"/>.</summary>
    public DatasetWriteSpec? WriteDataset { get; set; }

    /// <summary><see cref="StepAction.ExtractAll"/>.</summary>
    public HarvestSpec? Harvest { get; set; }

    /// <summary><see cref="StepAction.Aggregate"/>. Publishes its answer as the output named
    /// <c>value</c>.</summary>
    public AggregateSpec? Aggregate { get; set; }

    /// <summary>
    /// <see cref="StepAction.SetZoom"/>: the zoom level to apply, as a percentage. 100 is normal
    /// size; 60 shows more of a wide page at smaller text.
    /// <para>
    /// A whole number rather than a factor because it is what a browser's own zoom menu shows and
    /// what a person says out loud — "sixty percent", not "zero point six". Its own field rather
    /// than <see cref="Value"/> so the editor can offer the levels instead of asking for a number
    /// and hoping.
    /// </para>
    /// <para>
    /// The zoom belongs to the RUN, not to the step: it stays until another step changes it, and
    /// the engine re-applies it after a navigation so a page loaded later is not quietly back at
    /// 100%. A run that opens a second browser — because a task asked for a different profile —
    /// starts it at 100%, since that is a browser nobody has zoomed yet.
    /// </para>
    /// </summary>
    public int? ZoomPercent { get; set; }

    /// <summary>
    /// Engine settings overridden at this scope; null (the usual case) means "inherit everything".
    /// Resolved through global -> collection -> task -> step by EngineSettingsResolver.
    /// </summary>
    public EngineSettingsOverride? Settings { get; set; }
}

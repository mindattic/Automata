namespace Automata.Core.Automation.Model;

/// <summary>What a <see cref="StepAction.Wait"/> step is waiting for.</summary>
public enum WaitMode
{
    /// <summary>A fixed number of milliseconds.</summary>
    Duration,

    /// <summary>The next occurrence of a time of day, in a named timezone.</summary>
    UntilTimeOfDay,

    /// <summary>Until a condition over bound values holds. Needs run state, so the orchestrator
    /// handles it rather than the replay engine.</summary>
    UntilCondition,

    /// <summary>Until an external signal arrives. Orchestrator-handled.</summary>
    UntilSignal,
}

/// <summary>
/// A pause. Duration and UntilTimeOfDay are pure and local, so the replay engine performs them
/// directly; the other two need run state and are intercepted upstream.
/// </summary>
public sealed class WaitSpec
{
    public WaitMode Mode { get; set; } = WaitMode.Duration;

    public int? DurationMs { get; set; }

    public TimeOnly? TimeOfDay { get; set; }

    /// <summary>IANA or Windows id; null uses the machine's local zone.</summary>
    public string? TimeZoneId { get; set; }

    public ConditionSpec? Condition { get; set; }

    public int PollMs { get; set; } = 5000;

    public string? SignalName { get; set; }

    /// <summary>Overall cap for a condition/signal wait, in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    /// A wait longer than this checkpoints the run and releases its browser lane instead of
    /// holding one idle; a later scheduler tick resumes it. Default 15 minutes.
    /// </summary>
    public int ParkAfterMs { get; set; } = 900_000;
}

/// <summary>One comparison. Deliberately one — no and/or chaining — so a condition stays a record
/// the picker can render rather than an expression tree it cannot.</summary>
public enum ConditionOp
{
    Equals,
    NotEquals,
    Contains,
    NotEmpty,
    Empty,
    GreaterThan,
    LessThan,
    IsTrue,
    IsFalse,
}

public sealed class ConditionSpec
{
    public BindingRef Left { get; set; } = new();
    public ConditionOp Op { get; set; } = ConditionOp.NotEmpty;

    /// <summary>Unused by the unary operators (NotEmpty, Empty, IsTrue, IsFalse).</summary>
    public BindingRef? Right { get; set; }
}

/// <summary>Fan a step's children out over the rows of a dataset.</summary>
public sealed class ForEachSpec
{
    public BindingRef Source { get; set; } = new();

    /// <summary>The name children use to reach the current row, e.g. <c>row.sku</c>.</summary>
    public string RowVariableName { get; set; } = "row";

    /// <summary>Rows to process at once. Above 1 requires a browser lane per row, so it is
    /// bounded by the resolved MaxConcurrency ceiling.</summary>
    public int MaxConcurrency { get; set; } = 1;
}

/// <summary>Append or replace rows in a named dataset.</summary>
public sealed class DatasetWriteSpec
{
    public string DatasetName { get; set; } = "";

    /// <summary>"csv" or "json".</summary>
    public string Format { get; set; } = "csv";

    /// <summary>Column name to the value that fills it.</summary>
    public Dictionary<string, BindingRef> Columns { get; set; } = [];

    public bool Append { get; set; } = true;
}

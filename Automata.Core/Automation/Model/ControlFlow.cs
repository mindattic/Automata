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
    /// A wait longer than this checkpoints the run and closes its browser instead of holding one
    /// idle; a later scheduler tick resumes it. Default 15 minutes.
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

    /// <summary>
    /// The value is there at all — this row HAS that column, that step DID publish that output.
    /// <para>
    /// Distinct from <see cref="NotEmpty"/> on purpose, and the distinction is the whole point of
    /// having it. A JSON array is ragged: some objects carry a key and some do not, and asking
    /// whether a missing one is "empty" fails the run rather than answering, because a column that
    /// is not there is normally a mis-typed column name and worth refusing. <c>Exists</c> is how
    /// you say you MEANT to ask, so absence becomes an answer instead of an error.
    /// </para>
    /// </summary>
    Exists,

    /// <summary>The value is not there at all. The other half of <see cref="Exists"/>, so a ragged
    /// list can be branched on either way round.</summary>
    NotExists,
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

    /// <summary>
    /// The name this loop publishes a row's POSITION under, after the row variable: <c>row.#</c>.
    /// <para>
    /// One-based, so it is the same number the run log already prints ("row 3 of 12"). Published
    /// both bare and qualified, exactly as a column is, because every binding written anywhere —
    /// by the picker, by the Gherkin compiler — names a column bare, and a value a binding cannot
    /// reach is not a feature. A dataset that really has a column called <c>#</c> wins, because
    /// that one is data and this is bookkeeping.
    /// </para>
    /// </summary>
    public const string RowNumberKey = "#";
}

/// <summary>
/// What an <see cref="StepAction.Aggregate"/> step does to a dataset column.
/// <para>
/// Five, and no more. This is the one place arithmetic enters the step model, and it enters as a
/// closed list a picker can render — not as an expression language. The moment a sixth is a
/// formula rather than a name, a task stops being a record of what it does.
/// </para>
/// </summary>
public enum AggregateOp
{
    Sum,
    Count,
    Min,
    Max,
    Average,
}

/// <summary>
/// Read one column of a dataset and reduce it to a single number, published as this step's
/// <c>value</c> output for a later step to bind to.
/// <para>
/// The step that closes the other end of the loop: a for-each can fan out over a dataset and write
/// results back, but until now nothing in the product could answer "so what is the total?" — that
/// arithmetic lived in an acceptance script, outside the thing being demonstrated.
/// </para>
/// </summary>
public sealed class AggregateSpec
{
    /// <summary>The dataset to read. A file name, as everywhere else.</summary>
    public string DatasetName { get; set; } = "";

    /// <summary>Which of its columns.</summary>
    public string ColumnName { get; set; } = "";

    public AggregateOp Op { get; set; } = AggregateOp.Sum;
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

    /// <summary>
    /// Start the dataset fresh the first time THIS RUN writes to it, then append for the rest of
    /// the run. Only consulted when <see cref="Append"/> is set.
    /// <para>
    /// Without it a collecting loop is not repeatable: every run adds its rows to the ones the
    /// last run left, so running a task twice doubles its results and the only way back is to
    /// delete the file by hand. Clearing it before the loop is not expressible either — a for-each
    /// isolates each row deliberately, so no step inside one can know whether it is the first.
    /// </para>
    /// <para>
    /// "First write of the run" is a property of the RUN, not of the row or the step, which is why
    /// the claim lives on the run state and is settled inside the dataset's own write lock: the
    /// first row to write replaces the file, and every row after it appends to what that row
    /// started — including when the same run writes the file from more than one loop.
    /// </para>
    /// </summary>
    public bool ResetOnFirstWrite { get; set; }
}

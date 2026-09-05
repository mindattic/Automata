using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Execution;

/// <summary>
/// What one task in a collection hands to the next.
/// <para>
/// A collection runs its tasks in order, and the only reason that order matters is that a task can
/// use what an earlier one found. This is where that happens: a task declares outputs, the engine
/// publishes them when it finishes, the caller keeps them, and the next task's declared inputs are
/// filled from that record.
/// </para>
/// <para>
/// It is deliberately a CONTRACT between tasks and not a shared scope. Task 2 names an output of
/// task 1 — not a step inside it — so re-recording a step in task 1 cannot silently change what
/// task 2 receives, and reading task 2 on its own tells you everything it takes and from where.
/// </para>
/// <para>
/// Both callers use this: the headless runner and the desktop app run their collections through
/// the same resolution, because a pipeline that behaved differently in the window from how it
/// behaves at 3am would be worse than no pipeline at all.
/// </para>
/// </summary>
public static class TaskPipeline
{
    /// <summary>What every task published so far, keyed by task id then by output name.</summary>
    public sealed class Carried
    {
        private readonly Dictionary<string, Dictionary<string, string>> byTask = new(StringComparer.Ordinal);

        public void Record(string taskId, IReadOnlyDictionary<string, string> values)
        {
            // Replaced, not merged: a task that runs twice in one collection publishes what it
            // found the LAST time, which is the only answer that is true of the run so far.
            byTask[taskId] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGet(string taskId, string outputName, out string value)
        {
            value = "";
            return byTask.TryGetValue(taskId, out var values)
                && values.TryGetValue(outputName, out value!);
        }

        public bool Has(string taskId) => byTask.ContainsKey(taskId);
    }

    /// <summary>
    /// The values to run <paramref name="task"/> with, and what to say about them.
    /// <para>
    /// Precedence is fixed and it is the useful order: a value supplied directly beats a wiring,
    /// and a wiring beats the input's own default. Supplying <c>--input term=heron</c> for a task
    /// whose term is normally wired from an earlier one is how a single set is re-run by hand
    /// without editing anything.
    /// </para>
    /// <para>
    /// An unresolvable wiring is a NOTE, not a failure. The input falls back to its default, and if
    /// it has none the run fails at the step that needed it, naming the input — the same message a
    /// task run on its own gives, which is what keeps a wired task runnable on its own.
    /// </para>
    /// </summary>
    public static (Dictionary<string, string> Inputs, List<string> Notes) Resolve(
        TaskDefinition task,
        Carried carried,
        IReadOnlyDictionary<string, string>? supplied)
    {
        var inputs = supplied == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(supplied, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        foreach (var input in task.Inputs)
        {
            if (input.From is not { } from || string.IsNullOrWhiteSpace(input.Name)) continue;
            if (inputs.ContainsKey(input.Name))
            {
                notes.Add($"'{input.Name}' was supplied directly, so the value from " +
                          $"{Describe(from)} is not used.");
                continue;
            }

            if (carried.TryGet(from.TaskId, from.OutputName, out var value))
            {
                inputs[input.Name] = value;
                notes.Add($"'{input.Name}' ← {Describe(from)} = {Quote(value)}");
            }
            else if (carried.Has(from.TaskId))
            {
                notes.Add($"'{input.Name}' is wired to {Describe(from)}, which published nothing " +
                          "this run — falling back to its default.");
            }
            else
            {
                notes.Add($"'{input.Name}' is wired to {Describe(from)}, which has not run in this " +
                          "collection — falling back to its default.");
            }
        }

        return (inputs, notes);
    }

    private static string Describe(TaskOutputRef from) =>
        $"'{(string.IsNullOrWhiteSpace(from.TaskName) ? from.TaskId : from.TaskName)} → {from.OutputName}'";

    /// <summary>
    /// Long values are cut down: this goes into a run log line, and a carried value can be a whole
    /// harvested row. What matters in the log is that something was carried and roughly what.
    /// </summary>
    private static string Quote(string value)
    {
        var oneLine = value.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 60 ? $"\"{oneLine}\"" : $"\"{oneLine[..57]}…\"";
    }
}

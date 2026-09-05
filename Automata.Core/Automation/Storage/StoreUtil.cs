using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>Shared helpers for the store and the zip archive service.</summary>
internal static class StoreUtil
{
    /// <summary>Deep-clone via the canonical JSON round-trip — guarantees clone == what disk would give back.</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AutomataJson.Options), AutomataJson.Options)!;

    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>
    /// Fresh ids for a whole task's step tree, AND every reference to those ids rewritten to match.
    /// <para>
    /// Step ids are only unique within a task, so a copy has to be re-keyed: two tasks answering to
    /// one id would make a self-heal, a park address or a published output ambiguous. But a task is
    /// FULL of references to its own step ids — a binding to what an earlier step read, an
    /// <c>otherwise</c> that records which <c>if</c> it belongs to, a declared task output naming
    /// the step that produces it, a watching wait whose condition reads the element it watches — and
    /// re-keying without rewriting those left every one of them pointing at the ORIGINAL's step. The
    /// copy loaded, looked right in the editor, and failed at run time with "has not been produced
    /// yet" about a value the step directly above it publishes.
    /// </para>
    /// <para>
    /// So the two halves are one operation and there is no way to do only the first.
    /// </para>
    /// </summary>
    public static void ReidentifySteps(TaskDefinition task)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in Step.Flatten(task.Steps))
        {
            var fresh = NewId();
            if (!string.IsNullOrEmpty(step.Id)) map[step.Id] = fresh;
            step.Id = fresh;
        }
        RemapStepIds(task, map);
    }

    /// <summary>
    /// Rewrites every reference to a step id through <paramref name="map"/>. Anything not in the map
    /// is left exactly as it was, which is what makes this safe to run over a partial remap.
    /// </summary>
    public static void RemapStepIds(TaskDefinition task, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return;

        foreach (var output in task.Outputs)
            output.SourceStepId = Moved(output.SourceStepId, map) ?? output.SourceStepId;

        foreach (var step in Step.Flatten(task.Steps))
            step.PairedIfId = Moved(step.PairedIfId, map) ?? step.PairedIfId;

        WalkBindings(task, binding =>
            binding.SourceStepId = Moved(binding.SourceStepId, map) ?? binding.SourceStepId);
    }

    /// <summary>
    /// Rewrites every reference to a TASK id: a <c>runTask</c> step's callee, a declared input's
    /// wiring to an earlier task's output, and a cross-task step-output binding.
    /// <para>
    /// Needed wherever a set of tasks is copied together, because the copies have to point at each
    /// other rather than back at the originals. A duplicated collection whose task 2 still took its
    /// input from the original's task 1 fell back to a default on every run — reported, but only in
    /// a note nobody reads until the numbers are wrong.
    /// </para>
    /// </summary>
    public static void RemapTaskIds(TaskDefinition task, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return;

        foreach (var input in task.Inputs)
            if (input.From is { } from) from.TaskId = Moved(from.TaskId, map) ?? from.TaskId;

        foreach (var step in Step.Flatten(task.Steps))
            step.RunTaskId = Moved(step.RunTaskId, map) ?? step.RunTaskId;

        WalkBindings(task, binding =>
            binding.SourceTaskId = Moved(binding.SourceTaskId, map) ?? binding.SourceTaskId);
    }

    /// <summary>Where the map sends an id, or null when it says nothing about it.</summary>
    private static string? Moved(string? id, IReadOnlyDictionary<string, string> map) =>
        id != null && map.TryGetValue(id, out var moved) ? moved : null;

    /// <summary>
    /// Every <see cref="BindingRef"/> a task can hold, wherever it is sited.
    /// <para>
    /// One walk rather than one per caller, because the cost of the bug this fixes was that a
    /// binding SITE got missed — and a list of sites written out twice is a list that drifts. A new
    /// place to put a binding is added here once and both remaps follow it.
    /// </para>
    /// </summary>
    private static void WalkBindings(TaskDefinition task, Action<BindingRef> visit)
    {
        foreach (var step in Step.Flatten(task.Steps))
        {
            Each(step.Bindings);
            Each(step.RunTaskInputs);
            Each(step.WriteDataset?.Columns);
            Condition(step.Condition);
            Condition(step.Wait?.Condition);
            One(step.ForEach?.Source);
        }

        void Each(Dictionary<string, BindingRef>? bindings)
        {
            if (bindings == null) return;
            foreach (var binding in bindings.Values) One(binding);
        }

        void Condition(ConditionSpec? condition)
        {
            if (condition == null) return;
            One(condition.Left);
            One(condition.Right);
        }

        void One(BindingRef? binding)
        {
            if (binding != null) visit(binding);
        }
    }

    /// <summary>"Search Google" → "search-google"; safe for file names.</summary>
    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "unnamed" : slug.Length <= 60 ? slug : slug[..60];
    }

    /// <summary>
    /// Keeps a name usable as a single file name without slugging it — unlike <see cref="Slug"/>
    /// this preserves case and the extension, which matters for a dataset called "bought.csv".
    /// </summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "unnamed" : cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    /// <summary>"Work" taken → "Work (2)", then "Work (3)", … Case-insensitive.</summary>
    public static string UniqueName(string desired, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired)) return desired;
        for (var n = 2; ; n++)
        {
            var candidate = $"{desired} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

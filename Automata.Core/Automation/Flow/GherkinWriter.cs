using System.Text;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Flow;

/// <summary>
/// The result of rendering tasks back to Gherkin.
/// <para>
/// <see cref="IsLossy"/> is the honest half of the feature: Gherkin is Automata's authoring and
/// review surface, not a serialization format. Compiling in is total; rendering out is
/// best-effort, and a task that cannot round-trip says so instead of silently degrading when it
/// is recompiled.
/// </para>
/// </summary>
public sealed record GherkinWriteResult(string Text, bool IsLossy, IReadOnlyList<string> Reasons);

/// <summary>
/// Renders a collection's tasks as a feature file.
/// <para>
/// The phrase rendering here deliberately mirrors <see cref="StepDefinitionCatalog"/> rather than
/// trying to invert its regexes. That duplicates the vocabulary in two places, which is a real
/// cost — the round-trip test is what keeps them honest, and it is the usual way a printer and a
/// parser are kept in step.
/// </para>
/// </summary>
public static class GherkinWriter
{
    public static GherkinWriteResult Write(Collection collection, IReadOnlyList<TaskDefinition> tasks)
    {
        var reasons = new List<string>();
        var sb = new StringBuilder();

        WriteTags(sb, collection.Settings, "");
        sb.Append("Feature: ").Append(collection.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(collection.Description))
            sb.Append("  ").Append(collection.Description.Replace("\n", "\n  ")).Append('\n');

        foreach (var task in tasks)
        {
            sb.Append('\n');
            WriteTask(sb, task, reasons);
        }

        return new GherkinWriteResult(sb.ToString(), reasons.Count > 0, reasons);
    }

    private static void WriteTask(StringBuilder sb, TaskDefinition task, List<string> reasons)
    {
        WriteTags(sb, task.Settings, "  ");

        // A task that is one for-each over a dataset is exactly a Scenario Outline, and the
        // Examples name carries the dataset so the rows stay in their own editable file.
        var loop = task.Steps is [{ Action: StepAction.ForEach } only] ? only : null;
        var body = loop?.Children ?? task.Steps;

        sb.Append("  ").Append(loop == null ? "Scenario: " : "Scenario Outline: ").Append(task.Name).Append('\n');

        if (!string.IsNullOrWhiteSpace(task.StartUrl))
            sb.Append("    Given I open ").Append(FlowValues.Quote(task.StartUrl)).Append('\n');

        WriteSteps(sb, body, task, reasons, first: true);

        if (loop != null)
            sb.Append("\n    Examples: ").Append(loop.ForEach?.Source?.DatasetName ?? "rows.csv").Append('\n');
    }

    /// <summary>
    /// The inverse of the compiler's guard flattening: an <c>If</c> becomes a guard line and its
    /// children continue at the same level. Any other nesting has no Gherkin form, so it is
    /// recorded as a reason rather than quietly dropped.
    /// </summary>
    private static void WriteSteps(
        StringBuilder sb, IReadOnlyList<Step> steps, TaskDefinition task, List<string> reasons, bool first)
    {
        foreach (var step in steps)
        {
            var keyword = first ? "Given" : "And";
            first = false;

            var phrase = Phrase(step, reasons);
            if (phrase == null)
            {
                sb.Append("    # ").Append(step.Label).Append(" — no Gherkin form for ")
                  .Append(step.Action).Append('\n');
                reasons.Add($"'{step.Label}' ({step.Action}) has no Gherkin form");
                continue;
            }

            sb.Append("    ").Append(keyword).Append(' ').Append(phrase).Append('\n');

            if (step.Children.Count == 0) continue;

            if (step.Action == StepAction.If)
            {
                WriteSteps(sb, step.Children, task, reasons, first: false);
            }
            else
            {
                reasons.Add($"'{step.Label}' has substeps, which Gherkin's flat step list cannot express");
                WriteSteps(sb, step.Children, task, reasons, first: false);
            }
        }
    }

    private static string? Phrase(Step step, List<string> reasons)
    {
        var target = Target(step, reasons);
        var value = Value(step, "Value");

        return step.Action switch
        {
            StepAction.Navigate => "I open " + (Value(step, "Url") ?? FlowValues.Quote(step.Url)),
            StepAction.Click => "I click " + target,
            StepAction.TypeText => $"I type {value} into {target}",
            StepAction.SetValue => $"I set {target} to {value}",
            StepAction.PressEnter => step.Target == null ? "I press Enter" : "I press Enter in " + target,
            StepAction.Check => "I check " + target,
            StepAction.Uncheck => "I uncheck " + target,
            StepAction.SelectOption => $"I select {value} in {target}",
            StepAction.UploadFile => $"I upload {value} into {target}",
            StepAction.WaitForElement => "I wait for " + target,
            StepAction.AssertElement => $"{target} contains {value}",
            StepAction.ExtractText when step.Outputs is [{ Name: var name }, ..] && !string.IsNullOrWhiteSpace(name)
                => $"I extract text from {target} as {name}",
            StepAction.Wait => WaitPhrase(step),
            StepAction.If => GuardPhrase(step),
            StepAction.RunTask => "I run task " + FlowValues.Quote(RunTaskName(step)),
            StepAction.WriteDataset => WritePhrase(step),
            _ => null,
        };
    }

    private static string? WaitPhrase(Step step)
    {
        var spec = step.Wait;
        if (spec == null) return null;
        return spec.Mode switch
        {
            WaitMode.Duration => $"I wait {spec.DurationMs ?? 0}ms",
            WaitMode.UntilTimeOfDay when spec.TimeOfDay is { } t =>
                $"I wait until {t:HH\\:mm}" + (string.IsNullOrWhiteSpace(spec.TimeZoneId) ? "" : " " + FlowValues.Quote(spec.TimeZoneId)),
            _ => null,
        };
    }

    private static string? GuardPhrase(Step step)
    {
        var condition = step.Condition;
        if (condition == null) return null;
        var entry = StepDefinitionCatalog.Comparisons.FirstOrDefault(c => c.Op == condition.Op);
        if (entry.Phrase == null) return null;

        var left = FlowValues.Write(null, condition.Left);
        return entry.Unary
            ? $"{Bare(left)} {entry.Phrase}"
            : $"{Bare(left)} {entry.Phrase} {FlowValues.Write(null, condition.Right)}";
    }

    private static string? WritePhrase(Step step)
    {
        var spec = step.WriteDataset;
        if (spec == null || spec.Columns.Count == 0) return null;
        var assignments = spec.Columns.Select(kv => kv.Key + "=" + FlowValues.Write(null, kv.Value));
        return $"I write {FlowValues.Quote(spec.DatasetName)} with {string.Join(", ", assignments)}";
    }

    /// <summary>The compiler recovers a run-task reference from the label, so keep that shape.</summary>
    private static string RunTaskName(Step step) =>
        step.Label.StartsWith("Run task '", StringComparison.Ordinal)
            ? step.Label["Run task '".Length..].TrimEnd('\'')
            : step.RunTaskId ?? "";

    private static string Bare(string written) => written.Trim('"');

    private static string Value(Step step, string field)
    {
        var binding = step.Bindings != null && step.Bindings.TryGetValue(field, out var b) ? b : null;
        if (binding != null) return FlowValues.Write(null, binding);
        return FlowValues.Quote(field == "Url" ? step.Url : step.Value);
    }

    /// <summary>
    /// A written target can only carry a selector or a piece of text. A recorded fingerprint holds
    /// far more — id, tag, classes, xpath, nearby label — so rendering one is a real loss, and
    /// recompiling this text would produce a weaker step than the one it came from.
    /// </summary>
    private static string Target(Step step, List<string> reasons)
    {
        var fingerprint = step.Target;
        if (fingerprint == null) return "\"\"";

        var written = fingerprint.CssSelector
            ?? fingerprint.XPath
            ?? fingerprint.AriaLabel
            ?? fingerprint.NearbyLabelText
            ?? fingerprint.VisibleText
            ?? (string.IsNullOrEmpty(fingerprint.Id) ? null : "#" + fingerprint.Id)
            ?? "";

        if (IsRecorded(fingerprint))
            reasons.Add($"'{step.Label}' targets a recorded element; its full identity cannot be written as Gherkin");

        return FlowValues.Quote(written);
    }

    /// <summary>
    /// A fingerprint the recorder built carries more than a written target can: a tag plus classes
    /// or an id alongside a selector is the tell.
    /// </summary>
    private static bool IsRecorded(ElementFingerprint fingerprint) =>
        !string.IsNullOrEmpty(fingerprint.Id)
        || !string.IsNullOrEmpty(fingerprint.NameAttr)
        || !string.IsNullOrEmpty(fingerprint.TypeAttr)
        || fingerprint.ClassList.Count > 0
        || (!string.IsNullOrEmpty(fingerprint.Tag) && !string.IsNullOrEmpty(fingerprint.CssSelector));

    private static void WriteTags(StringBuilder sb, EngineSettingsOverride? settings, string indent)
    {
        if (settings == null) return;
        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.BrowserProfile)) tags.Add("@profile:" + settings.BrowserProfile);
        if (settings.MaxConcurrency is { } lanes) tags.Add("@concurrency:" + lanes);
        if (settings.Retry is { MaxAttempts: var attempts } && attempts > 1) tags.Add("@retry:" + attempts);
        if (settings.DefaultStepTimeoutMs is { } ms) tags.Add("@timeout:" + ms);
        if (settings.ContinueOnStepError == true) tags.Add("@continue-on-error");
        if (settings.SelfHeal == false) tags.Add("@self-heal:off");
        if (tags.Count > 0) sb.Append(indent).Append(string.Join(" ", tags)).Append('\n');
    }
}

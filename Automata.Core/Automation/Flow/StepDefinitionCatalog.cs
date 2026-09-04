using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Flow;

/// <summary>
/// A step the catalog recognised, plus the raw source text of anything that cannot be resolved
/// without knowing the rest of the feature.
/// <para>
/// Carrying these explicitly — rather than stuffing them into <see cref="Step.Value"/> and
/// decoding them later — keeps the catalog a pure phrase→shape mapping and leaves every decision
/// that needs context in one place, the compiler.
/// </para>
/// </summary>
public sealed record StepDraft
{
    public required Step Step { get; init; }

    /// <summary>A quoted value as written: a literal, an Examples placeholder, or a reference.</summary>
    public string? RawValue { get; init; }

    /// <summary>Guard operands, as written.</summary>
    public string? RawLeft { get; init; }
    public string? RawRight { get; init; }

    /// <summary>The <c>col=value, col=value</c> list of a write-dataset step.</summary>
    public string? RawAssignments { get; init; }

    /// <summary>The task name a <c>run task</c> step names; resolved to an id by the compiler.</summary>
    public string? RawTaskName { get; init; }
}

/// <summary>One recognised Gherkin phrase and what it compiles to.</summary>
public sealed record StepDefinition(string Phrase, Regex Pattern, Func<Match, StepDraft> Build);

/// <summary>
/// The closed vocabulary: every Gherkin step Automata understands, and nothing else.
/// <para>
/// This is the difference between adopting Gherkin and adopting Cucumber. In Cucumber the step
/// definitions are written by the user and the language is open; here the table ships fixed, which
/// is what makes validation total — an unrecognised phrase becomes a diagnostic with a line
/// number, never a guess.
/// </para>
/// <para>
/// The same table is rendered into the LLM's prompt, so what the model is told it may write and
/// what the compiler accepts cannot drift apart.
/// </para>
/// </summary>
public static class StepDefinitionCatalog
{
    // Everything the vocabulary takes as a target, value or path is double-quoted, which keeps the
    // grammar unambiguous without needing a real parser.
    private const string Q = "\"([^\"]*)\"";

    /// <summary>
    /// A bare reference: a captured value (<c>price</c>), the whole current row (<c>row</c>), or
    /// one of its columns (<c>row.sku</c>, <c>row.#</c>).
    /// <para>
    /// <c>#</c> is in the set because that is the name a row's position is published under. It is
    /// safe mid-line: Gherkin's comments are whole lines, so a <c>#</c> inside step text is text.
    /// </para>
    /// </summary>
    private const string Ref = @"([A-Za-z_][\w.#]*)";

    private static Regex Rx([StringSyntax(StringSyntaxAttribute.Regex)] string pattern) =>
        new("^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Comparison words for a guard step. Ordered longest-first so "is not empty" is not
    /// swallowed by "is not".</summary>
    public static readonly (string Phrase, ConditionOp Op, bool Unary)[] Comparisons =
    [
        ("is not empty", ConditionOp.NotEmpty, true),
        ("is empty", ConditionOp.Empty, true),
        // Negated first, so "is not present" is never read as "is present" with a stray word.
        ("is not present", ConditionOp.NotExists, true),
        ("is present", ConditionOp.Exists, true),
        ("is true", ConditionOp.IsTrue, true),
        ("is false", ConditionOp.IsFalse, true),
        ("is greater than", ConditionOp.GreaterThan, false),
        ("is less than", ConditionOp.LessThan, false),
        ("is exactly", ConditionOp.Equals, false),
        ("is not", ConditionOp.NotEquals, false),
        ("contains", ConditionOp.Contains, false),
    ];

    public static IReadOnlyList<StepDefinition> All { get; } = Build();

    private static List<StepDefinition> Build()
    {
        var defs = new List<StepDefinition>
        {
            Def($"I open {Q}", $@"I open {Q}", m => Draft(new Step
            {
                Action = StepAction.Navigate,
                Label = $"Go to {FlowValues.ShortUrl(m.Groups[1].Value)}",
                Url = m.Groups[1].Value,
            })),

            Def($"I click {Q}", $@"I click {Q}", m =>
                Draft(Targeted(StepAction.Click, m.Groups[1].Value, $"Click '{m.Groups[1].Value}'"))),

            Def($"I type {Q} into {Q}", $@"I type {Q} into {Q}", m => Draft(
                Targeted(StepAction.TypeText, m.Groups[2].Value, $"Type into {m.Groups[2].Value}"),
                rawValue: m.Groups[1].Value)),

            Def($"I set {Q} to {Q}", $@"I set {Q} to {Q}", m => Draft(
                Targeted(StepAction.SetValue, m.Groups[1].Value, $"Set {m.Groups[1].Value}"),
                rawValue: m.Groups[2].Value)),

            Def("I press Enter", @"I press Enter", _ => Draft(new Step
            {
                Action = StepAction.PressEnter,
                Label = "Press Enter",
            })),

            Def($"I press Enter in {Q}", $@"I press Enter in {Q}", m =>
                Draft(Targeted(StepAction.PressEnter, m.Groups[1].Value, $"Press Enter in {m.Groups[1].Value}"))),

            Def($"I check {Q}", $@"I check {Q}", m =>
                Draft(Targeted(StepAction.Check, m.Groups[1].Value, $"Check '{m.Groups[1].Value}'"))),

            Def($"I uncheck {Q}", $@"I uncheck {Q}", m =>
                Draft(Targeted(StepAction.Uncheck, m.Groups[1].Value, $"Uncheck '{m.Groups[1].Value}'"))),

            Def($"I select {Q} in {Q}", $@"I select {Q} in {Q}", m => Draft(
                Targeted(StepAction.SelectOption, m.Groups[2].Value, $"Select in {m.Groups[2].Value}"),
                rawValue: m.Groups[1].Value)),

            Def($"I upload {Q} into {Q}", $@"I upload {Q} into {Q}", m => Draft(
                Targeted(StepAction.UploadFile, m.Groups[2].Value, $"Upload into {m.Groups[2].Value}"),
                rawValue: m.Groups[1].Value)),

            Def($"I wait for {Q}", $@"I wait for {Q}", m =>
                Draft(Targeted(StepAction.WaitForElement, m.Groups[1].Value, $"Wait for {m.Groups[1].Value}"))),

            Def($"{Q} contains {Q}", $@"{Q} contains {Q}", m => Draft(
                Targeted(StepAction.AssertElement, m.Groups[1].Value,
                    $"{m.Groups[1].Value} contains '{m.Groups[2].Value}'"),
                rawValue: m.Groups[2].Value)),

            Def($"I extract text from {Q} as <name>", $@"I extract text from {Q} as ([A-Za-z_]\w*)", m =>
            {
                var step = Targeted(StepAction.ExtractText, m.Groups[1].Value,
                    $"Extract {m.Groups[2].Value} from {m.Groups[1].Value}");
                step.Outputs = [new OutputField { Name = m.Groups[2].Value }];
                return Draft(step);
            }),

            Def("I wait <n><ms|s|m|h>", @"I wait (\d+)\s*(ms|s|m|h)", m => Draft(new Step
            {
                Action = StepAction.Wait,
                Label = $"Wait {m.Groups[1].Value}{m.Groups[2].Value}",
                Wait = new WaitSpec
                {
                    Mode = WaitMode.Duration,
                    DurationMs = FlowValues.DurationMs(int.Parse(m.Groups[1].Value), m.Groups[2].Value),
                },
            })),

            Def($"I wait until <HH:mm> [{Q}]", @"I wait until (\d{1,2}:\d{2})(?:\s+" + Q + ")?", m => Draft(new Step
            {
                Action = StepAction.Wait,
                Label = $"Wait until {m.Groups[1].Value}",
                Wait = new WaitSpec
                {
                    Mode = WaitMode.UntilTimeOfDay,
                    TimeOfDay = TimeOnly.Parse(m.Groups[1].Value),
                    TimeZoneId = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null,
                },
            })),

            Def($"I run task {Q}", $@"I run task {Q}", m => Draft(new Step
            {
                Action = StepAction.RunTask,
                Label = $"Run task '{m.Groups[1].Value}'",
            }, rawTaskName: m.Groups[1].Value)),

            Def($"I write {Q} with <col>=<value>, …", $@"I write {Q} with (.+)", m => Draft(new Step
            {
                Action = StepAction.WriteDataset,
                Label = $"Write a row to {m.Groups[1].Value}",
                WriteDataset = new DatasetWriteSpec { DatasetName = m.Groups[1].Value, Append = true },
            }, rawAssignments: m.Groups[2].Value)),
        };

        // The other half of a guard. A bare word rather than a phrase about a value, because it is
        // not about a value — it is punctuation, closing the guard above it and opening its
        // opposite. Gherkin has no block syntax, so this is the block end.
        defs.Add(Def("otherwise", @"otherwise", _ => Draft(new Step
        {
            Action = StepAction.Else,
            Label = "Otherwise",
        })));

        // Guards come last, so `"total" contains "$"` is read as an assertion about the page
        // rather than a guard about a captured value. An assertion names an element; a guard names
        // a value, and only the quoting distinguishes them.
        foreach (var (phrase, op, unary) in Comparisons)
        {
            var escaped = Regex.Escape(phrase);
            var pattern = unary
                ? $@"{Ref}\s+{escaped}"
                : $@"{Ref}\s+{escaped}\s+(?:{Q}|{Ref})";
            var isUnary = unary;
            var thisOp = op;
            var thisPhrase = phrase;

            defs.Add(Def(
                isUnary ? $"<value> {phrase}" : $"<value> {phrase} {Q}",
                pattern,
                m =>
                {
                    var right = isUnary ? null
                        : m.Groups[2].Success ? "\"" + m.Groups[2].Value + "\""
                        : m.Groups[3].Value;
                    return Draft(
                        new Step
                        {
                            Action = StepAction.If,
                            Label = $"When {m.Groups[1].Value} {thisPhrase}" +
                                (right == null ? "" : $" {right.Trim('"')}"),
                            Condition = new ConditionSpec { Op = thisOp },
                        },
                        rawLeft: m.Groups[1].Value,
                        rawRight: right);
                }));
        }

        return defs;
    }

    /// <summary>The first definition whose pattern matches, or null.</summary>
    public static (StepDefinition Definition, Match Match)? Match(string text)
    {
        var trimmed = text.Trim();
        foreach (var def in All)
        {
            var match = def.Pattern.Match(trimmed);
            if (match.Success) return (def, match);
        }
        return null;
    }

    /// <summary>The vocabulary as prose — rendered into the LLM prompt and the help dialog, so what
    /// the model is told it may write and what the compiler accepts cannot drift apart.</summary>
    public static string Vocabulary() => string.Join("\n", All.Select(d => "  " + d.Phrase));

    // ---- construction helpers -----------------------------------------------------------------

    private static StepDefinition Def(
        string phrase, [StringSyntax(StringSyntaxAttribute.Regex)] string pattern, Func<Match, StepDraft> build) =>
        new(phrase, Rx(pattern), build);

    private static StepDraft Draft(
        Step step, string? rawValue = null, string? rawLeft = null, string? rawRight = null,
        string? rawAssignments = null, string? rawTaskName = null) => new()
    {
        Step = step,
        RawValue = rawValue,
        RawLeft = rawLeft,
        RawRight = rawRight,
        RawAssignments = rawAssignments,
        RawTaskName = rawTaskName,
    };

    private static Step Targeted(StepAction action, string target, string label) => new()
    {
        Action = action,
        Label = label,
        Target = FlowValues.TargetFor(target),
    };
}

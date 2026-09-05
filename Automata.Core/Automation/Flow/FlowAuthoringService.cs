using System.Text;
using System.Text.RegularExpressions;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Flow;

/// <summary>What one drafting attempt produced.</summary>
/// <param name="FeatureText">The Gherkin the model wrote — shown to the user whether or not it compiled.</param>
/// <param name="Result">The compile result for that text.</param>
/// <param name="Attempts">How many turns it took, including repairs.</param>
/// <param name="Provider">Which LLM answered, for the log.</param>
public sealed record FlowDraft(string FeatureText, FlowCompileResult Result, int Attempts, string Provider);

/// <summary>
/// Natural language in, a compiled collection out — with a Gherkin feature file in between that
/// the user can read before anything is saved.
/// <para>
/// The intermediate artifact is the point. Asking a model to emit a step tree directly makes it
/// responsible for ids, fingerprint shapes, binding wiring and nesting all at once, with nothing
/// checkable in between — a mistake is then either silently wrong or an opaque deserialization
/// error. Asking it for a short script in a documented, closed vocabulary is a job it is good at,
/// and every failure comes back as a diagnostic with a line number that can be handed straight
/// back for repair.
/// </para>
/// </summary>
public sealed partial class FlowAuthoringService
{
    /// <summary>
    /// Bounded on purpose. If three tries with the exact diagnostics have not produced something
    /// that compiles, the honest move is to show the user what went wrong rather than keep paying
    /// for guesses.
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly IReadOnlyList<IToolCallingLlm> providers;
    private readonly ILogger<FlowAuthoringService> log;

    public FlowAuthoringService(
        IReadOnlyList<IToolCallingLlm> providers,
        ILogger<FlowAuthoringService>? log = null)
    {
        this.providers = providers;
        this.log = log ?? NullLogger<FlowAuthoringService>.Instance;
    }

    /// <summary>
    /// Drafts a feature from a description and compiles it, repairing against its own diagnostics
    /// up to <see cref="MaxAttempts"/> times.
    /// </summary>
    /// <param name="description">What the user typed.</param>
    /// <param name="context">Live facts worth knowing: dataset names, existing task names.</param>
    public async Task<FlowDraft> DraftAsync(
        string description, FlowAuthoringContext context, CancellationToken ct = default)
    {
        var provider = await FirstConfiguredAsync();
        if (provider == null)
        {
            return new FlowDraft("", FlowCompileResult.Failed(new FlowDiagnostic(
                FlowSeverity.Error, 1, 1,
                "No LLM provider has usable credentials — add a key in Settings, or write the feature by hand.")),
                0, "none");
        }

        var history = new List<ToolLoopMessage> { new ToolLoopMessage.UserText(description) };
        var systemPrompt = SystemPrompt(context);
        var featureText = "";
        FlowCompileResult result = FlowCompileResult.Failed();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = await provider.CreateTurnAsync(systemPrompt, history, [], 4096, ct);
            featureText = StripFences(string.Concat(turn.Parts.OfType<AssistantPart.Text>().Select(t => t.Value)));

            result = GherkinFlowCompiler.Compile(featureText, context.CollectionName);
            if (!result.HasErrors)
            {
                log.LogInformation("Feature drafted by {Provider} on attempt {Attempt}", provider.Name, attempt);
                return new FlowDraft(featureText, result, attempt, provider.Name);
            }

            if (attempt == MaxAttempts) break;

            // Hand the exact diagnostics back. Line and column are what make this a repair rather
            // than a re-roll.
            history.Add(new ToolLoopMessage.AssistantTurn([new AssistantPart.Text(featureText)]));
            history.Add(new ToolLoopMessage.UserText(RepairPrompt(result)));
            log.LogInformation("Draft attempt {Attempt} did not compile; repairing", attempt);
        }

        return new FlowDraft(featureText, result, MaxAttempts, provider.Name);
    }

    private async Task<IToolCallingLlm?> FirstConfiguredAsync()
    {
        foreach (var provider in providers)
            if (await provider.IsConfiguredAsync())
                return provider;
        return null;
    }

    private static string RepairPrompt(FlowCompileResult result)
    {
        var sb = new StringBuilder("That did not compile. Fix exactly these problems and reply with the whole feature file again:\n");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == FlowSeverity.Error))
            sb.Append("  line ").Append(diagnostic.Line).Append(": ").Append(diagnostic.Message).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The vocabulary comes from <see cref="StepDefinitionCatalog"/> rather than being written out
    /// here, so what the model is told it may write and what the compiler accepts cannot drift.
    /// </summary>
    public static string SystemPrompt(FlowAuthoringContext context)
    {
        var sb = new StringBuilder();
        sb.Append("""
            You write Gherkin feature files for Automata, a browser automation tool. Reply with the
            feature file and NOTHING else — no explanation, no code fences.

            A Feature is a collection. Each Scenario is one task. Each step is one browser action.
            Use Background for steps every scenario in the file needs.

            You may ONLY use these step phrases. Anything else fails to compile:

            """);
        sb.Append(StepDefinitionCatalog.Vocabulary()).Append("\n\n");
        sb.Append("""
            Rules that matter:
              - Targets are quoted. "#id" or ".class" is a CSS selector; anything else is matched by
                its visible text or label, which is usually the better choice.
              - To use a value from the page later, capture it first with
                `I extract text from "<target>" as <name>`, then refer to it by that bare name.
              - Gherkin has no if-block. A comparison step acts as a guard: everything after it in
                the scenario only runs when it holds.
              - To repeat over rows, write a Scenario Outline with an Examples table and refer to
                columns as "<column>". Name the Examples block after a .csv file to use an existing
                dataset instead of the inline table.
              - Prefer pressing Enter over clicking a search button; search suggestion overlays make
                the button unreliable.

            Tags you may use: @profile:<name>, @retry:<n>, @timeout:<ms>,
            @continue-on-error, @self-heal:off. Scheduling tags are not supported yet.

            """);

        if (context.DatasetNames.Count > 0)
            sb.Append("Datasets available: ").Append(string.Join(", ", context.DatasetNames)).Append('\n');
        if (context.TaskNames.Count > 0)
            sb.Append("Existing task names you may reference with `I run task`: ")
              .Append(string.Join(", ", context.TaskNames)).Append('\n');
        if (!string.IsNullOrWhiteSpace(context.CurrentUrl))
            sb.Append("The browser is currently on: ").Append(context.CurrentUrl).Append('\n');

        return sb.ToString();
    }

    /// <summary>Models wrap code in fences however much you ask them not to; strip them rather than
    /// failing the parse on a backtick.</summary>
    internal static string StripFences(string text)
    {
        var trimmed = text.Trim();
        var match = FencedBlock().Match(trimmed);
        return (match.Success ? match.Groups[1].Value : trimmed).Trim();
    }

    [GeneratedRegex(@"^```(?:gherkin|feature|cucumber)?\s*\n([\s\S]*?)\n?```$", RegexOptions.IgnoreCase)]
    private static partial Regex FencedBlock();
}

/// <summary>Live facts the prompt should know, so the model names things that actually exist.</summary>
public sealed record FlowAuthoringContext
{
    public string? CollectionName { get; init; }
    public IReadOnlyList<string> DatasetNames { get; init; } = [];
    public IReadOnlyList<string> TaskNames { get; init; } = [];
    public string? CurrentUrl { get; init; }
}

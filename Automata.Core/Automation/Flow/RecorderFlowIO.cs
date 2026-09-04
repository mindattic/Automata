using System.Text.Json;
using System.Text.Json.Nodes;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Core.Automation.Flow;

/// <summary>A converted flow plus anything that could not be carried across.</summary>
public sealed record RecorderImportResult(TaskDefinition Task, IReadOnlyList<string> Warnings);

/// <summary>
/// Import and export of Chrome DevTools' Recorder format (the <c>@puppeteer/replay</c> schema).
/// <para>
/// Adopted rather than invented, for the same reason as Gherkin: it is a published schema for
/// exactly this problem, and its <c>selectors</c> array — a list of alternatives, each an
/// <c>aria/</c>, <c>text/</c>, <c>xpath/</c>, <c>pierce/</c> or plain CSS string — is essentially
/// Automata's own multi-strategy fingerprint written down by someone else. That correspondence is
/// what makes the conversion faithful rather than lossy guesswork.
/// </para>
/// <para>
/// Recording in Chrome and replaying in Automata therefore works, and vice versa, for the
/// overlapping subset. Steps outside that subset are reported, never silently dropped.
/// </para>
/// </summary>
public static class RecorderFlowIO
{
    public static RecorderImportResult Import(string json, string? taskName = null)
    {
        var warnings = new List<string>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex)
        {
            return new RecorderImportResult(
                new TaskDefinition { Name = taskName ?? "Imported recording" },
                [$"not valid JSON: {ex.Message}"]);
        }

        var flow = root?.AsObject();
        var title = flow?["title"]?.GetValue<string>();
        var task = new TaskDefinition
        {
            Name = taskName ?? (string.IsNullOrWhiteSpace(title) ? "Imported recording" : title!),
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
        };

        var steps = flow?["steps"]?.AsArray();
        if (steps == null)
            return new RecorderImportResult(task, ["no \"steps\" array — is this a Recorder export?"]);

        foreach (var node in steps)
        {
            var step = node?.AsObject();
            if (step == null) continue;
            var type = step["type"]?.GetValue<string>() ?? "";
            var converted = ConvertStep(type, step, warnings);
            if (converted != null)
            {
                converted.Id = StoreUtil.NewId();
                // The first navigate is the flow's starting point; keeping it as a step too would
                // just re-navigate to where the run already is.
                if (task.Steps.Count == 0 && converted.Action == StepAction.Navigate)
                {
                    task.StartUrl = converted.Url;
                    continue;
                }
                task.Steps.Add(converted);
            }
        }

        return new RecorderImportResult(task, warnings);
    }

    private static Step? ConvertStep(string type, JsonObject step, List<string> warnings)
    {
        switch (type.ToLowerInvariant())
        {
            case "navigate":
            {
                var url = step["url"]?.GetValue<string>() ?? "";
                return new Step
                {
                    Action = StepAction.Navigate,
                    Label = $"Go to {FlowValues.ShortUrl(url)}",
                    Url = url,
                };
            }

            case "click":
                return Targeted(StepAction.Click, step, warnings, "Click");

            case "change":
            {
                var value = step["value"]?.GetValue<string>() ?? "";
                var converted = Targeted(StepAction.SetValue, step, warnings, "Set");
                if (converted != null) converted.Value = value;
                return converted;
            }

            case "keydown":
            {
                var key = step["key"]?.GetValue<string>() ?? "";
                if (!key.Equals("Enter", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"keyDown '{key}' has no equivalent — only Enter is replayable as a key press");
                    return null;
                }
                return new Step { Action = StepAction.PressEnter, Label = "Press Enter" };
            }

            // The paired keyUp carries no extra meaning once keyDown became a key press.
            case "keyup":
                return null;

            case "waitforelement":
                return Targeted(StepAction.WaitForElement, step, warnings, "Wait for");

            default:
                warnings.Add($"'{type}' steps are not supported and were skipped");
                return null;
        }
    }

    private static Step? Targeted(StepAction action, JsonObject step, List<string> warnings, string verb)
    {
        var fingerprint = FingerprintFrom(step["selectors"]?.AsArray(), warnings);
        if (fingerprint == null)
        {
            warnings.Add($"a {action} step had no usable selector and was skipped");
            return null;
        }
        var name = fingerprint.AriaLabel ?? fingerprint.VisibleText ?? fingerprint.CssSelector ?? "element";
        return new Step { Action = action, Label = $"{verb} '{name}'", Target = fingerprint };
    }

    /// <summary>
    /// Folds the Recorder's list of selector alternatives into one fingerprint. Each alternative is
    /// a different strategy for the same element, which is exactly how the resolver already thinks.
    /// </summary>
    private static ElementFingerprint? FingerprintFrom(JsonArray? selectors, List<string> warnings)
    {
        if (selectors == null) return null;
        var fingerprint = new ElementFingerprint();
        var any = false;

        foreach (var alternative in selectors)
        {
            // An alternative is either a string or an array of strings (a shadow-DOM chain).
            var parts = alternative is JsonArray chain
                ? chain.Select(c => c?.GetValue<string>()).Where(c => c != null).Select(c => c!).ToList()
                : alternative?.GetValue<string>() is { } single ? [single] : [];

            if (parts.Count > 1)
            {
                warnings.Add("a selector pierces shadow DOM, which Automata does not support yet — using its first part only");
            }

            var selector = parts.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(selector)) continue;

            if (selector.StartsWith("aria/", StringComparison.Ordinal))
            {
                fingerprint.AriaLabel ??= selector[5..];
                any = true;
            }
            else if (selector.StartsWith("text/", StringComparison.Ordinal))
            {
                fingerprint.VisibleText ??= selector[5..];
                any = true;
            }
            else if (selector.StartsWith("xpath/", StringComparison.Ordinal))
            {
                fingerprint.XPath ??= selector[6..];
                any = true;
            }
            else if (selector.StartsWith("pierce/", StringComparison.Ordinal))
            {
                warnings.Add("a pierce/ selector was ignored — shadow DOM is a known limitation");
            }
            else
            {
                fingerprint.CssSelector ??= selector;
                any = true;
            }
        }

        return any ? fingerprint : null;
    }

    /// <summary>
    /// Renders a task as a Recorder flow. The fingerprint's strategies become the selector
    /// alternatives, most specific first, which is the order Chrome's own replayer prefers.
    /// </summary>
    public static string Export(TaskDefinition task)
    {
        var steps = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "setViewport",
                ["width"] = 1280,
                ["height"] = 900,
                ["deviceScaleFactor"] = 1,
                ["isMobile"] = false,
                ["hasTouch"] = false,
                ["isLandscape"] = false,
            },
        };

        if (!string.IsNullOrWhiteSpace(task.StartUrl))
            steps.Add(new JsonObject { ["type"] = "navigate", ["url"] = task.StartUrl });

        // Recorder flows are flat, so substeps are emitted in document order after their parent.
        void Walk(IEnumerable<Step> source)
        {
            foreach (var step in source)
            {
                var converted = ExportStep(step);
                if (converted != null) steps.Add(converted);
                Walk(step.Children);
            }
        }
        Walk(task.Steps);

        var flow = new JsonObject { ["title"] = task.Name, ["steps"] = steps };
        return flow.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject? ExportStep(Step step) => step.Action switch
    {
        StepAction.Navigate => new JsonObject { ["type"] = "navigate", ["url"] = step.Url ?? "" },
        StepAction.Click => WithSelectors(new JsonObject { ["type"] = "click" }, step),
        StepAction.SetValue or StepAction.TypeText =>
            WithSelectors(new JsonObject { ["type"] = "change", ["value"] = step.Value ?? "" }, step),
        StepAction.PressEnter => new JsonObject { ["type"] = "keyDown", ["key"] = "Enter" },
        StepAction.WaitForElement or StepAction.AssertElement =>
            WithSelectors(new JsonObject { ["type"] = "waitForElement" }, step),
        // Everything else is Automata-only: there is no Recorder shape for a control-flow step or
        // a dataset write, and inventing one would produce a file Chrome cannot replay.
        _ => null,
    };

    private static JsonObject? WithSelectors(JsonObject node, Step step)
    {
        var fingerprint = step.Target;
        if (fingerprint == null) return null;

        var selectors = new JsonArray();
        if (!string.IsNullOrWhiteSpace(fingerprint.CssSelector)) selectors.Add(fingerprint.CssSelector);
        else if (!string.IsNullOrWhiteSpace(fingerprint.Id)) selectors.Add("#" + fingerprint.Id);
        if (!string.IsNullOrWhiteSpace(fingerprint.AriaLabel)) selectors.Add("aria/" + fingerprint.AriaLabel);
        if (!string.IsNullOrWhiteSpace(fingerprint.VisibleText)) selectors.Add("text/" + fingerprint.VisibleText);
        if (!string.IsNullOrWhiteSpace(fingerprint.XPath)) selectors.Add("xpath/" + fingerprint.XPath);
        if (selectors.Count == 0) return null;

        node["selectors"] = selectors;
        return node;
    }
}

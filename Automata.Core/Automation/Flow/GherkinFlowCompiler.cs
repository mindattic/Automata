using Gherkin;
using Gherkin.Ast;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

// Gherkin.Ast has its own Step; ours is the one this file mostly means.
using Step = Automata.Core.Automation.Model.Step;

namespace Automata.Core.Automation.Flow;

/// <summary>
/// Compiles a Gherkin feature file into a collection of tasks.
/// <para>
/// The Gherkin package's own AST is used directly rather than being re-mapped into a private
/// document type: it already carries locations, tags, tables and doc strings, and an extra layer
/// would only be a place for the two to drift apart.
/// </para>
/// <para>
/// Two mappings are worth knowing before reading the code:
/// </para>
/// <list type="number">
/// <item><b>Feature → Collection, Scenario → Task, step → Step.</b> Nothing was bent to make that
/// line up; it is why Gherkin was adopted rather than a grammar invented.</item>
/// <item><b>Gherkin is flat; a step tree is nested.</b> Gherkin has no <c>if { }</c> block, so a
/// guard step becomes an <see cref="StepAction.If"/> and <em>the rest of the scenario becomes its
/// children</em>. That is the one non-obvious transform here, and <see cref="GherkinWriter"/>
/// inverts it.</item>
/// </list>
/// </summary>
public static class GherkinFlowCompiler
{
    public static FlowCompileResult Compile(string featureText, string? collectionName = null)
    {
        GherkinDocument document;
        try
        {
            document = new Parser().Parse(new StringReader(featureText));
        }
        catch (CompositeParserException ex)
        {
            // Syntax errors are reported with their own line and column so the authoring loop can
            // hand them straight back rather than saying "that didn't work".
            return FlowCompileResult.Failed(ex.Errors.Select(FromParserError).ToArray());
        }
        catch (ParserException ex)
        {
            return FlowCompileResult.Failed(FromParserError(ex));
        }

        if (document.Feature == null)
            return FlowCompileResult.Failed(new FlowDiagnostic(FlowSeverity.Error, 1, 1, "no Feature in this file"));

        var diagnostics = new List<FlowDiagnostic>();
        var feature = document.Feature;

        var collection = new Collection
        {
            Name = collectionName ?? (string.IsNullOrWhiteSpace(feature.Name) ? "Imported feature" : feature.Name),
            Description = feature.Description?.Trim() ?? "",
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
            Settings = ReadTags(feature.Tags, feature.Location, diagnostics),
        };

        var background = feature.Children.OfType<Background>().FirstOrDefault();
        var scenarios = feature.Children.OfType<Scenario>().ToList();
        // Rule groups scenarios; treat its scenarios as if they were written at feature level
        // rather than rejecting a perfectly ordinary Gherkin construct.
        foreach (var rule in feature.Children.OfType<Rule>())
            scenarios.AddRange(rule.Children.OfType<Scenario>());

        if (scenarios.Count == 0)
            diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, feature.Location.Line, feature.Location.Column,
                "a Feature needs at least one Scenario — each one becomes a task"));

        var tasks = new List<TaskDefinition>();
        var datasets = new List<InlineDataset>();

        foreach (var scenario in scenarios)
        {
            var task = CompileScenario(scenario, background, collection, diagnostics, datasets);
            if (task != null) tasks.Add(task);
        }

        ResolveTaskReferences(tasks, diagnostics);
        collection.TaskOrder = tasks.Select(t => t.Id).ToList();

        return diagnostics.Any(d => d.Severity == FlowSeverity.Error)
            ? new FlowCompileResult(null, [], [], diagnostics)
            : new FlowCompileResult(collection, tasks, datasets, diagnostics);
    }

    private static FlowDiagnostic FromParserError(Exception ex)
    {
        var location = (ex as ParserException)?.Location;
        return new FlowDiagnostic(FlowSeverity.Error, location?.Line ?? 1, location?.Column ?? 1, ex.Message);
    }

    private static TaskDefinition? CompileScenario(
        Scenario scenario,
        Background? background,
        Collection collection,
        List<FlowDiagnostic> diagnostics,
        List<InlineDataset> datasets)
    {
        var task = new TaskDefinition
        {
            CollectionId = collection.Id,
            Name = string.IsNullOrWhiteSpace(scenario.Name) ? "Scenario" : scenario.Name,
            Description = scenario.Description?.Trim() ?? "",
            CreatedUtc = DateTimeOffset.UtcNow,
            ModifiedUtc = DateTimeOffset.UtcNow,
            Settings = ReadTags(scenario.Tags, scenario.Location, diagnostics),
        };

        // Background runs before every scenario, so its steps are simply prepended.
        var sourceSteps = (background?.Steps ?? []).Concat(scenario.Steps).ToList();

        // Outputs declared so far, so a bare reference resolves to the step that published it.
        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var drafts = new List<(StepDraft Draft, Location Location)>();

        foreach (var step in sourceSteps)
        {
            var matched = StepDefinitionCatalog.Match(step.Text);
            if (matched == null)
            {
                diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, step.Location.Line, step.Location.Column,
                    $"no step matches \"{step.Text.Trim()}\" — see the vocabulary for what is understood"));
                continue;
            }
            var draft = matched.Value.Definition.Build(matched.Value.Match);
            draft.Step.Id = StoreUtil.NewId();
            drafts.Add((draft, step.Location));
        }

        // A "start" is implicit: an opening navigate becomes the task's StartUrl only when the
        // author wrote it first, which is what Background usually produces.
        var examples = scenario.Examples.FirstOrDefault();
        var built = BuildTree(drafts, outputs, examples != null, diagnostics);

        if (examples == null)
        {
            task.Steps = built;
            return task;
        }

        // Scenario Outline + Examples IS a for-each over a dataset. The Examples name, when it
        // looks like a file, points at a real dataset; otherwise the inline table becomes one, so
        // the rows stay editable afterwards rather than being frozen into the feature file.
        var datasetName = string.IsNullOrWhiteSpace(examples.Name)
            ? StoreUtil.Slug(task.Name) + "-examples.csv"
            : examples.Name.Trim();

        if (!datasetName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            && !datasetName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            datasetName = StoreUtil.Slug(datasetName) + ".csv";
        }

        var rows = ReadExamples(examples);
        if (rows.Count > 0) datasets.Add(new InlineDataset(datasetName, rows));
        else
            diagnostics.Add(new FlowDiagnostic(FlowSeverity.Warning, examples.Location.Line, examples.Location.Column,
                $"no rows in this Examples table — '{datasetName}' must already exist for the loop to do anything"));

        task.Steps =
        [
            new Step
            {
                Id = StoreUtil.NewId(),
                Action = StepAction.ForEach,
                Label = $"For each row in {datasetName}",
                ForEach = new ForEachSpec
                {
                    Source = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = datasetName },
                    RowVariableName = "row",
                },
                Children = built,
            },
        ];
        return task;
    }

    /// <summary>
    /// Flat-with-guards → nested. Everything after a guard becomes that guard's children, which is
    /// how idiomatic Gherkin expresses "only do the rest when…" without a block syntax.
    /// <para>
    /// An <c>otherwise</c> line splits that remainder in two: what the guard runs, and what runs
    /// when it does not hold. It is claimed by the INNERMOST guard still open — so the search for
    /// it stops at the next guard, because that one will take everything after itself anyway. That
    /// rule is what makes both shapes round-trip: a plain if/otherwise at the top, and an
    /// if/otherwise nested inside another guard's half.
    /// </para>
    /// </summary>
    private static List<Step> BuildTree(
        List<(StepDraft Draft, Location Location)> drafts,
        Dictionary<string, string> outputs,
        bool insideLoop,
        List<FlowDiagnostic> diagnostics)
    {
        var steps = new List<Step>();
        for (var i = 0; i < drafts.Count; i++)
        {
            var (draft, location) = drafts[i];
            var step = draft.Step;

            if (draft.RawValue != null)
                ApplyValue(step, "Value", draft.RawValue, outputs, insideLoop, location, diagnostics);

            if (draft.RawAssignments != null)
                ApplyAssignments(step, draft.RawAssignments, outputs, insideLoop, location, diagnostics);

            if (step.Action == StepAction.If)
            {
                step.Condition!.Left = Reference(draft.RawLeft!, outputs, insideLoop, location, diagnostics);
                if (draft.RawRight != null)
                    step.Condition.Right = Reference(draft.RawRight, outputs, insideLoop, location, diagnostics);

                // Everything after the guard belongs to it — up to its own `otherwise`, if it has
                // one it can still claim.
                var rest = drafts.Skip(i + 1).ToList();
                var split = -1;
                for (var k = 0; k < rest.Count; k++)
                {
                    var action = rest[k].Draft.Step.Action;
                    // A nested guard takes the remainder including any otherwise in it, so this one
                    // has already stopped being a candidate by the time we reach that word.
                    if (action == StepAction.If) break;
                    if (action == StepAction.Else) { split = k; break; }
                }

                if (split < 0)
                {
                    step.Children = BuildTree(rest, outputs, insideLoop, diagnostics);
                    steps.Add(step);
                    return steps;
                }

                step.Children = BuildTree(rest.Take(split).ToList(), outputs, insideLoop, diagnostics);
                steps.Add(step);

                var otherwise = rest[split].Draft.Step;
                otherwise.Children = BuildTree(
                    rest.Skip(split + 1).ToList(), outputs, insideLoop, diagnostics);
                steps.Add(otherwise);
                return steps;
            }

            // Reaching an `otherwise` HERE means no guard claimed it — there was none open. It is
            // kept, so nothing is silently dropped, and named, because the alternative is a branch
            // that never runs and never says why.
            if (step.Action == StepAction.Else)
            {
                diagnostics.Add(new FlowDiagnostic(
                    FlowSeverity.Error, location.Line, location.Column,
                    "'otherwise' has no 'if' before it to be the other half of"));
            }

            foreach (var output in step.Outputs ?? [])
                if (!string.IsNullOrWhiteSpace(output.Name))
                    outputs[output.Name] = step.Id;

            steps.Add(step);
        }
        return steps;
    }

    private static void ApplyValue(
        Step step, string field, string written, Dictionary<string, string> outputs,
        bool insideLoop, Location location, List<FlowDiagnostic> diagnostics)
    {
        if (FlowValues.IsPlaceholder(written))
        {
            var column = FlowValues.PlaceholderName(written);
            if (!insideLoop)
            {
                diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, location.Line, location.Column,
                    $"<{column}> only means something inside a Scenario Outline with an Examples table"));
                return;
            }
            step.Bindings ??= [];
            step.Bindings[field] = new BindingRef
            {
                Kind = BindingKind.DatasetColumn,
                ColumnName = column,
                Label = "row → " + column,
            };
            return;
        }
        step.Value = written;
    }

    private static void ApplyAssignments(
        Step step, string written, Dictionary<string, string> outputs,
        bool insideLoop, Location location, List<FlowDiagnostic> diagnostics)
    {
        var spec = step.WriteDataset!;
        foreach (var part in SplitAssignments(written))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, location.Line, location.Column,
                    $"'{part.Trim()}' is not a column assignment — write it as name=value"));
                continue;
            }
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            spec.Columns[name] = value.StartsWith('"')
                ? new BindingRef { Kind = BindingKind.Literal, Literal = value.Trim('"') }
                : Reference(value, outputs, insideLoop, location, diagnostics);
        }
        if (spec.Columns.Count == 0)
            diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, location.Line, location.Column,
                "a write step needs at least one column"));
    }

    /// <summary>Splits on commas that are not inside quotes, so a value may contain one.</summary>
    private static IEnumerable<string> SplitAssignments(string written)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i < written.Length; i++)
        {
            if (written[i] == '"') quoted = !quoted;
            else if (written[i] == ',' && !quoted)
            {
                yield return written[start..i];
                start = i + 1;
            }
        }
        if (start < written.Length) yield return written[start..];
    }

    /// <summary>
    /// Turns a written reference into a binding: a quoted string is a literal, <c>&lt;col&gt;</c>
    /// or <c>row.col</c> is a dataset column, <c>env.NAME</c> is an environment variable, and a
    /// bare name must be something an earlier step declared — checked here, not at run time.
    /// </summary>
    private static BindingRef Reference(
        string written, Dictionary<string, string> outputs, bool insideLoop,
        Location location, List<FlowDiagnostic> diagnostics)
    {
        var text = written.Trim();

        if (text.StartsWith('"'))
            return new BindingRef { Kind = BindingKind.Literal, Literal = text.Trim('"') };

        if (FlowValues.IsPlaceholder(text))
            text = "row." + FlowValues.PlaceholderName(text);

        if (text.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
        {
            var name = text[4..];
            return new BindingRef { Kind = BindingKind.EnvVar, EnvVarName = name, Label = "env: " + name };
        }

        if (text.StartsWith("row.", StringComparison.OrdinalIgnoreCase))
        {
            var column = text[4..];
            if (!insideLoop)
            {
                diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, location.Line, location.Column,
                    $"'{text}' needs a Scenario Outline — there is no row here"));
            }
            return new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = column, Label = "row → " + column };
        }

        if (!outputs.TryGetValue(text, out var sourceStepId))
        {
            diagnostics.Add(new FlowDiagnostic(FlowSeverity.Error, location.Line, location.Column,
                $"nothing has captured '{text}' yet — add an \"I extract text from … as {text}\" step before this one"));
            return new BindingRef { Kind = BindingKind.Literal, Literal = "" };
        }

        return new BindingRef
        {
            Kind = BindingKind.StepOutput,
            SourceStepId = sourceStepId,
            OutputField = text,
            Label = text,
        };
    }

    private static List<Dictionary<string, string>> ReadExamples(Examples examples)
    {
        var header = examples.TableHeader?.Cells.Select(c => c.Value.Trim()).ToList();
        if (header == null || header.Count == 0) return [];

        var rows = new List<Dictionary<string, string>>();
        foreach (var row in examples.TableBody ?? [])
        {
            var cells = row.Cells.Select(c => c.Value).ToList();
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Count; i++) dict[header[i]] = i < cells.Count ? cells[i].Trim() : "";
            rows.Add(dict);
        }
        return rows;
    }

    /// <summary>A <c>run task</c> step names a task; ids only exist once every task is built.</summary>
    private static void ResolveTaskReferences(List<TaskDefinition> tasks, List<FlowDiagnostic> diagnostics)
    {
        var byName = tasks.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks) Walk(task.Steps);

        void Walk(List<Step> steps)
        {
            foreach (var step in steps)
            {
                if (step.Action == StepAction.RunTask && step.Label.StartsWith("Run task '", StringComparison.Ordinal))
                {
                    var name = step.Label["Run task '".Length..].TrimEnd('\'');
                    if (byName.TryGetValue(name, out var id)) step.RunTaskId = id;
                    else
                        diagnostics.Add(new FlowDiagnostic(FlowSeverity.Warning, 0, 0,
                            $"'{name}' is not a Scenario in this file — pick the task in the editor before running"));
                }
                Walk(step.Children);
            }
        }
    }

    /// <summary>
    /// Tags become scoped engine settings. Only tags whose settings the engine honours today are
    /// accepted; scheduling tags are reported rather than silently dropped, because a schedule that
    /// looks applied but is not would be worse than one that is refused.
    /// </summary>
    private static EngineSettingsOverride? ReadTags(
        IEnumerable<Tag> tags, Location location, List<FlowDiagnostic> diagnostics)
    {
        var settings = new EngineSettingsOverride();
        foreach (var tag in tags)
        {
            var text = tag.Name.TrimStart('@');
            var colon = text.IndexOf(':');
            var key = (colon < 0 ? text : text[..colon]).ToLowerInvariant();
            var value = colon < 0 ? "" : text[(colon + 1)..];

            switch (key)
            {
                case "profile": settings.BrowserProfile = value; break;
                case "concurrency" when int.TryParse(value, out var lanes): settings.MaxConcurrency = lanes; break;
                case "retry" when int.TryParse(value, out var attempts):
                    settings.Retry = new RetryPolicy { MaxAttempts = attempts };
                    break;
                case "timeout" when int.TryParse(value, out var ms): settings.DefaultStepTimeoutMs = ms; break;
                case "continue-on-error": settings.ContinueOnStepError = true; break;
                case "self-heal" when value.Equals("off", StringComparison.OrdinalIgnoreCase):
                    settings.SelfHeal = false;
                    break;
                case "at":
                case "after":
                case "every":
                    diagnostics.Add(new FlowDiagnostic(FlowSeverity.Warning, tag.Location.Line, tag.Location.Column,
                        $"@{text} describes a schedule, and scheduling is not built yet — the task compiles, but nothing will trigger it"));
                    break;
                default:
                    diagnostics.Add(new FlowDiagnostic(FlowSeverity.Warning, tag.Location.Line, tag.Location.Column,
                        $"@{text} is not a tag Automata understands — ignored"));
                    break;
            }
        }
        return settings.IsEmpty ? null : settings;
    }
}

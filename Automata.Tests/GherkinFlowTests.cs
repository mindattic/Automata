using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Model;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class GherkinFlowTests
{
    private static FlowCompileResult Compile(string feature) => GherkinFlowCompiler.Compile(feature);

    private static string Errors(FlowCompileResult r) =>
        string.Join(" | ", r.Diagnostics.Select(d => d.ToString()));

    // ---- the hierarchy maps straight across ------------------------------------------------------

    [Test]
    public void FeatureBecomesACollection_AndEachScenarioBecomesATask()
    {
        var result = Compile("""
            Feature: Supplier restock

              Scenario: Sign in
                Given I open "https://shop.example/login"
                And I click "Sign in"

              Scenario: Check stock
                Given I open "https://shop.example/stock"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        Assert.Multiple(() =>
        {
            Assert.That(result.Collection!.Name, Is.EqualTo("Supplier restock"));
            Assert.That(result.Tasks.Select(t => t.Name), Is.EqualTo(new[] { "Sign in", "Check stock" }));
            Assert.That(result.Tasks[0].Steps, Has.Count.EqualTo(2));
            Assert.That(result.Tasks[0].Steps[0].Action, Is.EqualTo(StepAction.Navigate));
            Assert.That(result.Collection.TaskOrder, Is.EqualTo(result.Tasks.Select(t => t.Id)));
        });
    }

    [Test]
    public void BackgroundStepsRunBeforeEveryScenario()
    {
        var result = Compile("""
            Feature: F

              Background:
                Given I open "https://x.example"

              Scenario: One
                Given I click "Alpha"

              Scenario: Two
                Given I click "Beta"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        Assert.That(result.Tasks.Select(t => t.Steps.Count), Is.EqualTo(new[] { 2, 2 }));
        Assert.That(result.Tasks[1].Steps[0].Action, Is.EqualTo(StepAction.Navigate));
    }

    // ---- the two non-obvious mappings ------------------------------------------------------------

    /// <summary>
    /// Gherkin has no block syntax, so a guard step takes the rest of the scenario as its children.
    /// This is the transform the whole design rests on.
    /// </summary>
    [Test]
    public void AGuardStepTakesTheRestOfTheScenarioAsItsChildren()
    {
        var result = Compile("""
            Feature: F

              Scenario: Buy when cheap
                Given I extract text from ".total" as price
                When price is less than "20"
                And I click "Add to cart"
                And I click "Checkout"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var steps = result.Tasks[0].Steps;
        Assert.Multiple(() =>
        {
            Assert.That(steps, Has.Count.EqualTo(2), "extract, then the guard — the clicks moved inside it");
            Assert.That(steps[1].Action, Is.EqualTo(StepAction.If));
            Assert.That(steps[1].Condition!.Op, Is.EqualTo(ConditionOp.LessThan));
            Assert.That(steps[1].Children, Has.Count.EqualTo(2));
            Assert.That(steps[1].Children[1].Label, Does.Contain("Checkout"));
        });
    }

    /// <summary>A written target cannot invent a recorded fingerprint, so it produces a partial one
    /// the resolver's tail strategies handle and self-heal later upgrades.</summary>
    [Test]
    public void APlainTargetBecomesAPartialTextFingerprint()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I click "Images"
            """);

        var target = result.Tasks[0].Steps[0].Target!;
        Assert.Multiple(() =>
        {
            Assert.That(target.VisibleText, Is.EqualTo("Images"));
            Assert.That(target.AriaLabel, Is.EqualTo("Images"));
            Assert.That(target.CssSelector, Is.Null, "plain words are not a selector");
        });
    }

    [Test]
    public void ASelectorTargetIsKeptAsASelector()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I wait for "#search"
            """);

        Assert.That(result.Tasks[0].Steps[0].Target!.CssSelector, Is.EqualTo("#search"));
        Assert.That(result.Tasks[0].Steps[0].Target!.VisibleText, Is.Null);
    }

    // ---- scenario outline is a for-each ----------------------------------------------------------

    [Test]
    public void AScenarioOutlineBecomesAForEachOverItsExamples()
    {
        var result = Compile("""
            Feature: F

              Scenario Outline: Each sku
                Given I open "https://shop.example"
                And I type "<sku>" into "Search"

                Examples:
                  | sku    |
                  | WT-100 |
                  | WT-200 |
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var loop = result.Tasks[0].Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(loop.Action, Is.EqualTo(StepAction.ForEach));
            Assert.That(loop.Children, Has.Count.EqualTo(2));
            Assert.That(result.Datasets, Has.Count.EqualTo(1));
            Assert.That(result.Datasets[0].Rows, Has.Count.EqualTo(2));
            Assert.That(result.Datasets[0].Rows[0]["sku"], Is.EqualTo("WT-100"));
            Assert.That(loop.ForEach!.Source.DatasetName, Is.EqualTo(result.Datasets[0].Name));
        });
    }

    [Test]
    public void AnExamplesPlaceholderBecomesADatasetColumnBinding()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given I type "<sku>" into "Search"
                Examples:
                  | sku |
                  | a   |
            """);

        var typed = result.Tasks[0].Steps[0].Children[0];
        Assert.That(typed.Bindings!["Value"].Kind, Is.EqualTo(BindingKind.DatasetColumn));
        Assert.That(typed.Bindings["Value"].ColumnName, Is.EqualTo("sku"));
    }

    /// <summary>An Examples block named like a file points at a real dataset instead of writing one.</summary>
    [Test]
    public void AnExamplesBlockNamedLikeAFilePointsAtThatDataset()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given I type "<sku>" into "Search"

                Examples: skus.csv
                  | sku |
                  | a   |
            """);

        Assert.That(result.Tasks[0].Steps[0].ForEach!.Source.DatasetName, Is.EqualTo("skus.csv"));
    }

    [Test]
    public void APlaceholderOutsideAnOutlineIsAnError()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I type "<sku>" into "Search"
            """);

        Assert.That(result.HasErrors, Is.True);
        Assert.That(Errors(result), Does.Contain("Scenario Outline"));
    }

    // ---- validation is total ---------------------------------------------------------------------

    [Test]
    public void AnUnrecognisedPhraseIsADiagnosticWithItsLine_NotAGuess()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I open "https://x.example"
                And I do something nobody defined
            """);

        Assert.That(result.HasErrors, Is.True);
        var error = result.Diagnostics.Single(d => d.Severity == FlowSeverity.Error);
        Assert.Multiple(() =>
        {
            Assert.That(error.Line, Is.EqualTo(4), "the diagnostic must point at the offending line");
            Assert.That(error.Message, Does.Contain("nobody defined"));
        });
    }

    [Test]
    public void ASyntaxErrorIsReportedWithItsLocationRatherThanThrowing()
    {
        var result = Compile("Scenario: no feature here\n  Given I click \"x\"\n");

        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Collection, Is.Null);
        Assert.That(result.Diagnostics, Is.Not.Empty);
    }

    [Test]
    public void ReferencingAValueNothingCapturedIsAnError()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given price is less than "20"
            """);

        Assert.That(result.HasErrors, Is.True);
        Assert.That(Errors(result), Does.Contain("nothing has captured 'price'"));
    }

    [Test]
    public void AReferenceResolvesToTheStepThatPublishedIt()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I extract text from ".total" as price
                When price is not empty
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var extract = result.Tasks[0].Steps[0];
        var guard = result.Tasks[0].Steps[1];
        Assert.That(guard.Condition!.Left.Kind, Is.EqualTo(BindingKind.StepOutput));
        Assert.That(guard.Condition.Left.SourceStepId, Is.EqualTo(extract.Id));
    }

    // ---- tags ------------------------------------------------------------------------------------

    [Test]
    public void TagsBecomeScopedEngineSettings()
    {
        var result = Compile("""
            @profile:work @concurrency:3
            Feature: F

              @retry:2 @continue-on-error
              Scenario: S
                Given I click "Go"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        Assert.Multiple(() =>
        {
            Assert.That(result.Collection!.Settings!.BrowserProfile, Is.EqualTo("work"));
            Assert.That(result.Collection.Settings.MaxConcurrency, Is.EqualTo(3));
            Assert.That(result.Tasks[0].Settings!.Retry!.MaxAttempts, Is.EqualTo(2));
            Assert.That(result.Tasks[0].Settings!.ContinueOnStepError, Is.True);
        });
    }

    /// <summary>
    /// A schedule tag that looked applied but was not would be worse than one that is refused, so
    /// it compiles with a warning rather than being silently dropped.
    /// </summary>
    [Test]
    public void AScheduleTagWarnsThatSchedulingIsNotBuiltYet()
    {
        var result = Compile("""
            @at:09:00
            Feature: F
              Scenario: S
                Given I click "Go"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        Assert.That(result.Diagnostics.Any(d => d.Severity == FlowSeverity.Warning
            && d.Message.Contains("scheduling is not built yet")), Is.True, Errors(result));
    }

    // ---- write dataset ---------------------------------------------------------------------------

    [Test]
    public void AWriteStepBuildsItsColumnsFromLiteralsAndReferences()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I extract text from ".total" as price
                And I write "bought.csv" with sku="WT-100", paid=price
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var spec = result.Tasks[0].Steps[1].WriteDataset!;
        Assert.Multiple(() =>
        {
            Assert.That(spec.DatasetName, Is.EqualTo("bought.csv"));
            Assert.That(spec.Columns["sku"].Kind, Is.EqualTo(BindingKind.Literal));
            Assert.That(spec.Columns["sku"].Literal, Is.EqualTo("WT-100"));
            Assert.That(spec.Columns["paid"].Kind, Is.EqualTo(BindingKind.StepOutput));
        });
    }

    [Test]
    public void RunTaskResolvesToASiblingScenarioByName()
    {
        var result = Compile("""
            Feature: F

              Scenario: Report
                Given I click "Send"

              Scenario: Main
                Given I run task "Report"
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var report = result.Tasks.Single(t => t.Name == "Report");
        var main = result.Tasks.Single(t => t.Name == "Main");
        Assert.That(main.Steps[0].RunTaskId, Is.EqualTo(report.Id));
    }

    // ---- round trip ------------------------------------------------------------------------------

    /// <summary>
    /// The load-bearing property: what the compiler produces, the writer can render, and compiling
    /// that again gives the same shape. It is what stops the two vocabularies drifting apart.
    /// </summary>
    [Test]
    public void CompileThenWriteThenCompileGivesTheSameShape()
    {
        const string source = """
            Feature: Round trip

              Scenario: Buy when cheap
                Given I open "https://shop.example"
                And I click "Images"
                And I extract text from ".total" as price
                When price is less than "20"
                And I click "Add to cart"
                And I write "bought.csv" with paid=price
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        Assert.That(written.IsLossy, Is.False, string.Join(" | ", written.Reasons));

        var second = Compile(written.Text);
        Assert.That(second.HasErrors, Is.False, Errors(second) + "\n---\n" + written.Text);

        Assert.That(Shape(second.Tasks), Is.EqualTo(Shape(first.Tasks)), written.Text);
    }

    [Test]
    public void AnOutlineRoundTripsAsAnOutline()
    {
        var first = Compile("""
            Feature: F

              Scenario Outline: Each
                Given I type "<sku>" into "Search"

                Examples: skus.csv
                  | sku |
                  | a   |
            """);

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        var second = Compile(written.Text);

        Assert.That(second.HasErrors, Is.False, Errors(second) + "\n---\n" + written.Text);
        Assert.That(written.Text, Does.Contain("Scenario Outline"));
        Assert.That(second.Tasks[0].Steps[0].ForEach!.Source.DatasetName, Is.EqualTo("skus.csv"));
    }

    /// <summary>
    /// A recorded task renders for reading but is flagged lossy: a fingerprint carries far more
    /// than a written target can, so recompiling it would produce a weaker step.
    /// </summary>
    [Test]
    public void ARecordedFingerprintMakesTheFeatureViewLossy()
    {
        var collection = new Collection { Name = "Recorded" };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Action = StepAction.Click, Label = "Click 'Go'",
                    Target = new ElementFingerprint
                    {
                        Id = "go", Tag = "button", CssSelector = "#go",
                        ClassList = ["btn", "primary"], VisibleText = "Go",
                    },
                },
            ],
        };

        var written = GherkinWriter.Write(collection, [task]);

        Assert.That(written.IsLossy, Is.True);
        Assert.That(written.Reasons.Single(), Does.Contain("recorded element"));
        Assert.That(written.Text, Does.Contain("I click \"#go\""), "it still renders for reading");
    }

    [Test]
    public void AStepWithSubstepsIsFlaggedBecauseGherkinIsFlat()
    {
        var collection = new Collection { Name = "Nested" };
        var task = new TaskDefinition
        {
            Name = "T",
            Steps =
            [
                new Step
                {
                    Action = StepAction.Group, Label = "Verify",
                    Children = [new Step { Action = StepAction.Click, Label = "Click", Target = FlowValues.TargetFor("Go") }],
                },
            ],
        };

        var written = GherkinWriter.Write(collection, [task]);

        Assert.That(written.IsLossy, Is.True);
        Assert.That(string.Join(" ", written.Reasons), Does.Contain("no Gherkin form"));
    }

    /// <summary>Every phrase the catalog advertises must actually match itself once its slots are
    /// filled — otherwise the LLM is told a form the compiler will reject.</summary>
    [Test]
    public void EveryCatalogPhraseIsMatchedByTheCatalog()
    {
        var samples = new[]
        {
            "I open \"https://x.example\"", "I click \"Go\"", "I type \"a\" into \"Search\"",
            "I set \"Field\" to \"a\"", "I press Enter", "I press Enter in \"Search\"",
            "I check \"Terms\"", "I uncheck \"Terms\"", "I select \"One\" in \"Pick\"",
            "I upload \"c:\\\\f.png\" into \"File\"", "I wait for \"#search\"",
            "\"#total\" contains \"$\"", "I extract text from \".total\" as price",
            "I wait 30s", "I wait until 14:00", "I run task \"Other\"",
            "I write \"out.csv\" with a=\"b\"",
            "price is not empty", "price is empty", "price is true", "price is false",
            "price is greater than \"1\"", "price is less than \"20\"",
            "price is exactly \"x\"", "price is not \"x\"", "price contains \"x\"",
        };

        var unmatched = samples.Where(s => StepDefinitionCatalog.Match(s) == null).ToList();
        Assert.That(unmatched, Is.Empty, "these advertised forms do not match the catalog");
    }

    [Test]
    public void TheVocabularyIsNonEmptyAndRendersOnePhrasePerLine()
    {
        var vocabulary = StepDefinitionCatalog.Vocabulary();

        Assert.That(vocabulary.Split('\n'), Has.Length.EqualTo(StepDefinitionCatalog.All.Count));
    }

    // A structural fingerprint of a task tree: actions and nesting, ignoring generated ids.
    private static string Shape(IReadOnlyList<TaskDefinition> tasks) =>
        string.Join("\n", tasks.Select(t => t.Name + ":" + Shape(t.Steps, 0)));

    private static string Shape(IReadOnlyList<Step> steps, int depth) =>
        string.Join("", steps.Select(s =>
            "\n" + new string(' ', depth * 2) + s.Action + Shape(s.Children, depth + 1)));
}

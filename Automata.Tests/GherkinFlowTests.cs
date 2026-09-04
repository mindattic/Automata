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

    /// <summary>
    /// Where a called task starts is a choice the step makes, so it has to be a choice the feature
    /// file can carry — otherwise rendering a task that made it would quietly drop it.
    /// </summary>
    [Test]
    public void RunningATaskFromItsOwnStartPageIsSaidOutLoudAndReadsBack()
    {
        const string source = """
            Feature: F

              Scenario: Report
                Given I click "Send"

              Scenario: Main
                Given I run task "Report" from its start page
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));
        Assert.That(first.Tasks.Single(t => t.Name == "Main").Steps[0].RunTaskOpensStartUrl, Is.True);

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        Assert.That(written.Text, Does.Contain("from its start page"), written.Text);

        var second = Compile(written.Text);
        Assert.That(second.Tasks.Single(t => t.Name == "Main").Steps[0].RunTaskOpensStartUrl, Is.True,
            written.Text);
    }

    /// <summary>And the plain phrase still means the rule it always meant.</summary>
    [Test]
    public void RunningATaskWithoutSayingSoStartsWhereTheCallerLeftOff()
    {
        var result = Compile("""
            Feature: F

              Scenario: Report
                Given I click "Send"

              Scenario: Main
                Given I run task "Report"
            """);

        Assert.That(result.Tasks.Single(t => t.Name == "Main").Steps[0].RunTaskOpensStartUrl, Is.False);
    }

    // ---- what a loop knows that its columns do not ------------------------------------------------

    /// <summary>
    /// The position rides on the column syntax rather than getting one of its own — <c>row.#</c> is
    /// a reference like any other, so the guard grammar, the placeholder form and the write
    /// assignments all took it without a new rule.
    /// </summary>
    [Test]
    public void TheRowsPositionReadsAsAnOrdinaryColumnReference()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given row.# is not empty
                And I write "out.csv" with at=<#>
                Examples: skus.csv
                  | sku |
                  | a   |
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var guard = result.Tasks[0].Steps[0].Children[0];
        var write = guard.Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(guard.Condition!.Left.ColumnName, Is.EqualTo("#"));
            Assert.That(write.WriteDataset!.Columns["at"].Kind, Is.EqualTo(BindingKind.DatasetColumn));
            Assert.That(write.WriteDataset.Columns["at"].ColumnName, Is.EqualTo("#"));
        });
    }

    /// <summary>
    /// A nested field needed no new syntax either: its name simply has a dot in it, and the
    /// reference grammar already allowed one. <c>row.</c> comes off the front and the rest is the
    /// column, however many dots it carries.
    /// </summary>
    [Test]
    public void ANestedFieldIsANameWithADotInIt()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given row.Contact.Email is not empty
                And I type "<Contact.Email>" into "To"
                Examples: people.json
                  | Name |
                  | Ada  |
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var guard = result.Tasks[0].Steps[0].Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(guard.Condition!.Left.ColumnName, Is.EqualTo("Contact.Email"));
            Assert.That(guard.Children[0].Bindings!["Value"].ColumnName, Is.EqualTo("Contact.Email"));
        });
    }

    /// <summary><c>row.sku</c> is a column; <c>row</c> on its own is the row.</summary>
    [Test]
    public void ABareRowInsideALoopIsTheWholeRow()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given I write "out.csv" with source=row, sku=<sku>
                Examples: skus.csv
                  | sku |
                  | a   |
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var columns = result.Tasks[0].Steps[0].Children[0].WriteDataset!.Columns;
        Assert.Multiple(() =>
        {
            Assert.That(columns["source"].Kind, Is.EqualTo(BindingKind.DatasetRow));
            Assert.That(columns["sku"].Kind, Is.EqualTo(BindingKind.DatasetColumn));
        });
    }

    /// <summary>Outside a loop there is no row for the word to mean, so it stays an ordinary bare
    /// name — and the diagnostic is the one about a value nothing has captured.</summary>
    [Test]
    public void ABareRowOutsideALoopIsStillJustAName()
    {
        var result = Compile("""
            Feature: F
              Scenario: S
                Given I write "out.csv" with source=row
            """);

        Assert.That(Errors(result), Does.Contain("row"));
        Assert.That(Errors(result), Does.Contain("captured"));
    }

    /// <summary>
    /// A write step's columns are the other slot that takes a bare reference, and it used to
    /// render them as <c>sku="&lt;sku&gt;"</c> — which the compiler read back as a LITERAL string
    /// of angle brackets. The binding survived the render and stopped being a binding.
    /// </summary>
    [Test]
    public void AColumnWrittenIntoADatasetStaysABindingAcrossARoundTrip()
    {
        const string source = """
            Feature: Round trip

              Scenario Outline: Copy a column
                Given I write "out.csv" with sku=<sku>
                Examples: skus.csv
                  | sku |
                  | a   |
            """;

        var written = GherkinWriter.Write(Compile(source).Collection!, Compile(source).Tasks);
        var again = Compile(written.Text);

        Assert.That(again.HasErrors, Is.False, Errors(again));
        var column = again.Tasks[0].Steps[0].Children[0].WriteDataset!.Columns["sku"];
        Assert.Multiple(() =>
        {
            Assert.That(column.Kind, Is.EqualTo(BindingKind.DatasetColumn), written.Text);
            Assert.That(column.ColumnName, Is.EqualTo("sku"), written.Text);
        });
    }

    /// <summary>The same file written the old way still means what it said — a feature already on
    /// disk must not turn its column into the text "&lt;sku&gt;".</summary>
    [Test]
    public void AQuotedPlaceholderInAWriteAssignmentIsStillTheColumn()
    {
        var result = Compile("""
            Feature: F
              Scenario Outline: S
                Given I write "out.csv" with sku="<sku>"
                Examples: skus.csv
                  | sku |
                  | a   |
            """);

        var column = result.Tasks[0].Steps[0].Children[0].WriteDataset!.Columns["sku"];
        Assert.Multiple(() =>
        {
            Assert.That(column.Kind, Is.EqualTo(BindingKind.DatasetColumn));
            Assert.That(column.ColumnName, Is.EqualTo("sku"));
        });
    }

    /// <summary>
    /// Both of them render to something the compiler reads back as what it was. The position goes
    /// out as a placeholder and the row as a bare word — deliberately not <c>&lt;row&gt;</c>, which
    /// would come back as a column called "row".
    /// </summary>
    [Test]
    public void APositionAndAWholeRowSurviveARoundTrip()
    {
        const string source = """
            Feature: Round trip

              Scenario Outline: Record where each row came from
                Given I write "out.csv" with at=<#>, source=row
                Examples: skus.csv
                  | sku |
                  | a   |
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        var second = Compile(written.Text);
        Assert.That(second.HasErrors, Is.False,
            Errors(second) + Environment.NewLine + "---" + Environment.NewLine + written.Text);

        var before = first.Tasks[0].Steps[0].Children[0].WriteDataset!.Columns;
        var after = second.Tasks[0].Steps[0].Children[0].WriteDataset!.Columns;
        Assert.Multiple(() =>
        {
            Assert.That(after["at"].Kind, Is.EqualTo(before["at"].Kind), written.Text);
            Assert.That(after["at"].ColumnName, Is.EqualTo(before["at"].ColumnName), written.Text);
            Assert.That(after["source"].Kind, Is.EqualTo(before["source"].Kind), written.Text);
        });
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

    // ---- otherwise -------------------------------------------------------------------------------
    //
    // Gherkin has no block syntax, so this codebase's rule is "a guard takes the rest of the
    // scenario". `otherwise` is the one thing that splits that rest in two, and these pin which
    // guard claims it — the answer that makes both shapes round-trip.

    [Test]
    public void OtherwiseSplitsAGuardIntoItsTwoHalves()
    {
        var result = Compile("""
            Feature: F

              Scenario Outline: Fill it in when there is one
                Given I open "https://x.example"
                And row.Name is present
                And I type "<Name>" into "Name"
                But otherwise
                And I click "Skip"

                Examples: roster.json
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        var steps = result.Tasks[0].Steps[0].Children;

        Assert.Multiple(() =>
        {
            Assert.That(steps[^2].Action, Is.EqualTo(StepAction.If));
            Assert.That(steps[^2].Condition!.Op, Is.EqualTo(ConditionOp.Exists));
            Assert.That(steps[^2].Children, Has.Count.EqualTo(1));
            Assert.That(steps[^1].Action, Is.EqualTo(StepAction.Else),
                "the otherwise is the guard's SIBLING, not one of its children");
            Assert.That(steps[^1].Children, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void AGuardWithAnOtherwiseRoundTrips()
    {
        var source = """
            Feature: F

              Scenario Outline: Fill it in when there is one
                Given I open "https://x.example"
                And row.Name is present
                And I type "<Name>" into "Name"
                But otherwise
                And I click "Skip"

                Examples: roster.json
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        Assert.That(written.IsLossy, Is.False, string.Join(" | ", written.Reasons));
        Assert.That(written.Text, Does.Contain("But otherwise"));

        var second = Compile(written.Text);
        Assert.That(second.HasErrors, Is.False, Errors(second) + "\n---\n" + written.Text);
        Assert.That(Shape(second.Tasks), Is.EqualTo(Shape(first.Tasks)), written.Text);
    }

    /// <summary>
    /// The rule that decides ownership: the search for an `otherwise` stops at the next guard,
    /// because that guard takes everything after itself anyway. So the INNER one claims it — and
    /// the shape has to survive being written back out and read again.
    /// </summary>
    [Test]
    public void AnOtherwiseBelongsToTheInnermostGuardStillOpen()
    {
        var source = """
            Feature: F

              Scenario Outline: Two guards deep
                Given I open "https://x.example"
                And row.Role is present
                And I click "Start"
                And row.Name is not present
                And I click "Skip"
                But otherwise
                And I type "<Name>" into "Name"

                Examples: roster.json
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));

        var outer = first.Tasks[0].Steps[0].Children[^1];
        Assert.Multiple(() =>
        {
            Assert.That(outer.Action, Is.EqualTo(StepAction.If), "the outer guard");
            Assert.That(outer.Children[^2].Action, Is.EqualTo(StepAction.If), "the inner guard");
            Assert.That(outer.Children[^1].Action, Is.EqualTo(StepAction.Else),
                "and the otherwise sits beside the INNER guard, inside the outer one");
        });

        var written = GherkinWriter.Write(first.Collection!, first.Tasks);
        var second = Compile(written.Text);
        Assert.That(second.HasErrors, Is.False, Errors(second) + "\n---\n" + written.Text);
        Assert.That(Shape(second.Tasks), Is.EqualTo(Shape(first.Tasks)), written.Text);
    }

    /// <summary>
    /// The compiler is the one place that knows which guard claimed an <c>otherwise</c>, so it is
    /// the place that records it — and it has to survive being written back out and read again, or
    /// the guarantee would last exactly as long as nobody opened the feature view.
    /// </summary>
    [Test]
    public void AnOtherwiseRemembersWhichGuardClaimedIt()
    {
        var source = """
            Feature: F

              Scenario Outline: Fill it in when there is one
                Given I open "https://x.example"
                And row.Name is present
                And I type "<Name>" into "Name"
                But otherwise
                And I click "Skip"

                Examples: roster.json
            """;

        var first = Compile(source);
        Assert.That(first.HasErrors, Is.False, Errors(first));

        var body = first.Tasks[0].Steps[0].Children;
        Assert.That(body[^1].PairedIfId, Is.EqualTo(body[^2].Id),
            "the otherwise should name the guard immediately before it");

        var second = Compile(GherkinWriter.Write(first.Collection!, first.Tasks).Text);
        var reBody = second.Tasks[0].Steps[0].Children;
        Assert.That(reBody[^1].PairedIfId, Is.EqualTo(reBody[^2].Id),
            "and still name it after a round-trip");
    }

    [Test]
    public void AnOtherwiseWithNoGuardBeforeItIsRefused()
    {
        var result = Compile("""
            Feature: F

              Scenario: Nothing to be the other half of
                Given I open "https://x.example"
                But otherwise
                And I click "Skip"
            """);

        Assert.That(result.HasErrors, Is.True);
        Assert.That(Errors(result), Does.Contain("no 'if' before it"));
    }

    [Test]
    public void PresenceReadsBackAsPresence()
    {
        var result = Compile("""
            Feature: F

              Scenario Outline: Ragged
                Given I open "https://x.example"
                And row.Name is not present
                And I click "Skip"

                Examples: roster.json
            """);

        Assert.That(result.HasErrors, Is.False, Errors(result));
        Assert.That(result.Tasks[0].Steps[0].Children[^1].Condition!.Op, Is.EqualTo(ConditionOp.NotExists),
            "'is not present' must not be read as 'is present' with a stray word");
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

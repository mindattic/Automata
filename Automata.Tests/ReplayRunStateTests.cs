using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ReplayRunStateTests
{
    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    [Test]
    public void ForkForRow_CopiesOutputsVariablesInputsAndZoom()
    {
        var state = new ReplayRunState { ZoomPercent = 60 };
        state.Outputs["step1\0total"] = "42";
        state.Variables["preexisting"] = "value";
        state.Rows["other"] = """{"a":"1"}""";
        state.PushInputs(new Dictionary<string, string> { ["name"] = "caller-input" });

        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");

        Assert.Multiple(() =>
        {
            Assert.That(child.Outputs["step1\0total"], Is.EqualTo("42"));
            Assert.That(child.Variables["preexisting"], Is.EqualTo("value"));
            Assert.That(child.Rows["other"], Is.EqualTo("""{"a":"1"}"""));
            Assert.That(child.Input("name"), Is.EqualTo("caller-input"));
            Assert.That(child.ZoomPercent, Is.EqualTo(60));
        });
    }

    [Test]
    public void ForkForRow_RowWritesDoNotLeakBackToParent()
    {
        var state = new ReplayRunState();

        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");
        child.Outputs["new\0field"] = "value";
        child.Variables["extra"] = "value";
        child.Rows["items"] = """{"sku":"changed"}""";

        Assert.Multiple(() =>
        {
            Assert.That(state.Outputs, Does.Not.ContainKey("new\0field"));
            Assert.That(state.Variables, Does.Not.ContainKey("extra"));
            Assert.That(state.Rows, Does.Not.ContainKey("items"));
        });
    }

    [Test]
    public void ForkForRow_PublishesRowNumberBareAndQualified()
    {
        var state = new ReplayRunState();

        var child = state.ForkForRow("row", Row(("sku", "abc")), 3, "items");

        Assert.Multiple(() =>
        {
            Assert.That(child.Variables[ForEachSpec.RowNumberKey], Is.EqualTo("3"));
            Assert.That(child.Variables["row." + ForEachSpec.RowNumberKey], Is.EqualTo("3"));
        });
    }

    [Test]
    public void ForkForRow_ARealColumnNamedHashWinsOverTheRowNumber()
    {
        var state = new ReplayRunState();

        var child = state.ForkForRow("row", Row((ForEachSpec.RowNumberKey, "actual-column-value")), 7, "items");

        Assert.That(child.Variables[ForEachSpec.RowNumberKey], Is.EqualTo("actual-column-value"));
    }

    [Test]
    public void ForkForRow_PublishesEachColumnBareAndQualified()
    {
        var state = new ReplayRunState();

        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");

        Assert.Multiple(() =>
        {
            Assert.That(child.Variables["sku"], Is.EqualTo("abc"));
            Assert.That(child.Variables["row.sku"], Is.EqualTo("abc"));
        });
    }

    [Test]
    public void TryRow_WithNoDatasetNameReturnsInnermostRow()
    {
        var state = new ReplayRunState();
        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");

        var found = child.TryRow(null, out var json);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(json, Is.EqualTo(child.InnermostRow));
        });
    }

    [Test]
    public void TryRow_WithANamedDatasetReturnsThatLoopsRow()
    {
        var state = new ReplayRunState();
        var outer = state.ForkForRow("outer", Row(("sku", "outer-sku")), 1, "outerSet");
        var inner = outer.ForkForRow("inner", Row(("qty", "5")), 1, "innerSet");

        var foundOuter = inner.TryRow("outerSet", out var outerJson);
        var foundInner = inner.TryRow("innerSet", out var innerJson);
        var foundMissing = inner.TryRow("noSuchSet", out var missingJson);

        Assert.Multiple(() =>
        {
            Assert.That(foundOuter, Is.True);
            Assert.That(outerJson, Is.EqualTo("""{"sku":"outer-sku"}"""));
            Assert.That(foundInner, Is.True);
            Assert.That(innerJson, Is.EqualTo("""{"qty":"5"}"""));
            Assert.That(foundMissing, Is.False);
            Assert.That(missingJson, Is.Null);
        });
    }

    [Test]
    public void TryRow_WithNoDatasetNameAndNoEnclosingLoopReturnsFalse()
    {
        var state = new ReplayRunState();

        var found = state.TryRow(null, out var json);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(json, Is.Null);
        });
    }

    [Test]
    public void MergeFrom_TalliesPassedAndHealedAndTakesTheChildsZoom()
    {
        var state = new ReplayRunState { Passed = 2, Healed = 1, ZoomPercent = 100 };
        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");
        child.Passed = 3;
        child.Healed = 2;
        child.ZoomPercent = 75;

        state.MergeFrom(child);

        Assert.Multiple(() =>
        {
            Assert.That(state.Passed, Is.EqualTo(5));
            Assert.That(state.Healed, Is.EqualTo(3));
            Assert.That(state.ZoomPercent, Is.EqualTo(75));
        });
    }

    [Test]
    public void MergeFrom_DoesNotCopyOutputsOrVariablesBackToTheParent()
    {
        var state = new ReplayRunState();
        var child = state.ForkForRow("row", Row(("sku", "abc")), 1, "items");
        child.Outputs["s\0f"] = "value";
        child.Variables["extra"] = "value";

        state.MergeFrom(child);

        Assert.Multiple(() =>
        {
            Assert.That(state.Outputs, Does.Not.ContainKey("s\0f"));
            Assert.That(state.Variables, Does.Not.ContainKey("extra"));
        });
    }

    [Test]
    public void MergeFrom_PropagatesFailedAndStopWhenTheChildSetThem()
    {
        var failedState = new ReplayRunState();
        var failedChild = failedState.ForkForRow("row", Row(("sku", "a")), 1, "items");
        failedChild.Failed = true;
        failedState.MergeFrom(failedChild);

        var stoppedState = new ReplayRunState();
        var stoppedChild = stoppedState.ForkForRow("row", Row(("sku", "a")), 1, "items");
        stoppedChild.Stop = true;
        stoppedState.MergeFrom(stoppedChild);

        Assert.Multiple(() =>
        {
            Assert.That(failedState.Failed, Is.True);
            Assert.That(stoppedState.Stop, Is.True);
        });
    }

    [Test]
    public void MergeFrom_DoesNotClearAnAlreadyFailedOrStoppedParent()
    {
        var state = new ReplayRunState { Failed = true, Stop = true };
        var child = state.ForkForRow("row", Row(("sku", "a")), 1, "items");

        state.MergeFrom(child);

        Assert.Multiple(() =>
        {
            Assert.That(state.Failed, Is.True);
            Assert.That(state.Stop, Is.True);
        });
    }

    [Test]
    public void ClaimFirstWrite_IsTrueOnlyTheFirstTime()
    {
        var state = new ReplayRunState();

        var first = state.ClaimFirstWrite("orders");
        var second = state.ClaimFirstWrite("orders");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
        });
    }

    [Test]
    public void ClaimFirstWrite_IsSharedAcrossForkedRows()
    {
        var state = new ReplayRunState();
        var rowA = state.ForkForRow("row", Row(("sku", "a")), 1, "items");
        var rowB = state.ForkForRow("row", Row(("sku", "b")), 2, "items");

        var claimedByA = rowA.ClaimFirstWrite("orders");
        var claimedByBAfterA = rowB.ClaimFirstWrite("orders");
        var claimedByParentAfterBoth = state.ClaimFirstWrite("orders");

        Assert.Multiple(() =>
        {
            Assert.That(claimedByA, Is.True);
            Assert.That(claimedByBAfterA, Is.False);
            Assert.That(claimedByParentAfterBoth, Is.False);
        });
    }

    [Test]
    public void RestoreClaims_ReSeedsClaimsFromACheckpoint()
    {
        var state = new ReplayRunState();

        state.RestoreClaims(["orders", "customers"]);

        Assert.Multiple(() =>
        {
            Assert.That(state.ClaimFirstWrite("orders"), Is.False);
            Assert.That(state.ClaimFirstWrite("customers"), Is.False);
            Assert.That(state.ClaimFirstWrite("invoices"), Is.True);
        });
    }

    [Test]
    public void RestoreClaims_WithNullDoesNothing()
    {
        var state = new ReplayRunState();

        state.RestoreClaims(null);

        Assert.That(state.ClaimFirstWrite("orders"), Is.True);
    }

    [Test]
    public void ClaimedDatasets_ReturnsEveryNameClaimedSoFar()
    {
        var state = new ReplayRunState();
        state.ClaimFirstWrite("orders");
        state.ClaimFirstWrite("customers");

        var claimed = state.ClaimedDatasets();

        Assert.That(claimed, Is.EquivalentTo(new[] { "orders", "customers" }));
    }

    [Test]
    public void PushInputs_MakesTheCalledTasksInputsVisible()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["name"] = "caller" });

        state.PushInputs(new Dictionary<string, string> { ["name"] = "callee" });

        Assert.That(state.Input("name"), Is.EqualTo("callee"));
    }

    [Test]
    public void PopInputs_RestoresTheCallersInputScope()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["name"] = "caller" });
        state.PushInputs(new Dictionary<string, string> { ["name"] = "callee" });

        state.PopInputs();

        Assert.That(state.Input("name"), Is.EqualTo("caller"));
    }

    [Test]
    public void PopInputs_NeverPopsTheOutermostRunScope()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["name"] = "run-level" });

        state.PopInputs();
        state.PopInputs();

        Assert.That(state.Input("name"), Is.EqualTo("run-level"));
    }

    [Test]
    public void OverlayRunInputs_OverwritesNamedValuesButLeavesOthersInPlace()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["a"] = "default-a", ["b"] = "default-b" });

        state.OverlayRunInputs(new Dictionary<string, string> { ["a"] = "resumed-a" });

        Assert.Multiple(() =>
        {
            Assert.That(state.Input("a"), Is.EqualTo("resumed-a"));
            Assert.That(state.Input("b"), Is.EqualTo("default-b"));
        });
    }

    [Test]
    public void OverlayRunInputs_WithNullDoesNothing()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["a"] = "default-a" });

        state.OverlayRunInputs(null);

        Assert.That(state.Input("a"), Is.EqualTo("default-a"));
    }

    [Test]
    public void SetRunInputs_ReplacesWhatWasThereBefore()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["a"] = "first", ["b"] = "stays-unless-cleared" });

        state.SetRunInputs(new Dictionary<string, string> { ["a"] = "second" });

        Assert.Multiple(() =>
        {
            Assert.That(state.Input("a"), Is.EqualTo("second"));
            Assert.That(state.Input("b"), Is.Null);
        });
    }

    [Test]
    public void SplitOutputKey_RoundTripsWithOutputKey()
    {
        var key = ReplayRunState.OutputKey("step-1", "total");

        var (stepId, field) = ReplayRunState.SplitOutputKey(key);

        Assert.Multiple(() =>
        {
            Assert.That(stepId, Is.EqualTo("step-1"));
            Assert.That(field, Is.EqualTo("total"));
        });
    }

    [Test]
    public void SplitOutputKey_WithNoSeparatorTreatsTheWholeKeyAsTheStepId()
    {
        var (stepId, field) = ReplayRunState.SplitOutputKey("no-separator-here");

        Assert.Multiple(() =>
        {
            Assert.That(stepId, Is.EqualTo("no-separator-here"));
            Assert.That(field, Is.EqualTo(""));
        });
    }
}

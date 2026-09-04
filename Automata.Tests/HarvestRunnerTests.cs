using NUnit.Framework;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;

namespace Automata.Tests;

/// <summary>
/// The rules that decide whether a page harvest is trustworthy enough to become a dataset. These
/// run with no browser anywhere near them, which is the point of keeping the shaping pure: the
/// judgements worth getting right are the ones about blank columns and duplicate keys, not about
/// CDP.
/// </summary>
[TestFixture]
public class HarvestRunnerTests
{
    private static HarvestSpec Spec(params string[] fieldNames) => new()
    {
        ItemSelector = "li.product",
        DatasetName = "products.csv",
        Fields = [.. fieldNames.Select(n => new HarvestField { Name = n })],
    };

    private static Dictionary<string, string> Row(params (string Key, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in cells) row[key] = value;
        return row;
    }

    // ---- validation -------------------------------------------------------------------------

    [Test]
    public void AHarvestWithNoRowsChosenIsRefused()
    {
        var spec = Spec("id");
        spec.ItemSelector = "";

        Assert.That(HarvestRunner.Validate(spec), Does.Contain("pick one item"));
    }

    [Test]
    public void AHarvestWithNoColumnsIsRefused()
    {
        var spec = Spec();

        Assert.That(HarvestRunner.Validate(spec), Does.Contain("no columns"));
    }

    [Test]
    public void AHarvestWithNoDatasetIsRefused()
    {
        var spec = Spec("id");
        spec.DatasetName = "";

        Assert.That(HarvestRunner.Validate(spec), Does.Contain("dataset"));
    }

    [Test]
    public void TwoColumnsWithTheSameNameAreRefused()
    {
        var spec = Spec("price", "PRICE");

        Assert.That(HarvestRunner.Validate(spec), Does.Contain("both called"));
    }

    [Test]
    public void AnAttributeColumnWithNoAttributeNamedIsRefused()
    {
        var spec = Spec("id");
        spec.Fields[0].Source = HarvestSource.Attribute;

        Assert.That(HarvestRunner.Validate(spec), Does.Contain("no attribute is named"));
    }

    [Test]
    public void AWellFormedHarvestPassesValidation()
    {
        Assert.That(HarvestRunner.Validate(Spec("id", "price")), Is.Null);
    }

    // ---- shaping ----------------------------------------------------------------------------

    [Test]
    public void EveryMatchedRowIsKeptWhenNothingIsDeduplicatedOrCapped()
    {
        var result = HarvestRunner.Shape(
            Spec("id"),
            [Row(("id", "a")), Row(("id", "b")), Row(("id", "c"))],
            []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Rows, Has.Count.EqualTo(3));
            Assert.That(result.MatchedRows, Is.EqualTo(3));
        });
    }

    /// <summary>
    /// The dangerous case: rows matched, so the step looks successful, but every column is blank —
    /// and a ForEach over the result would then loop the right number of times over nothing and
    /// report a clean pass.
    /// </summary>
    [Test]
    public void RowsThatMatchedButFilledNoColumnAreRefused()
    {
        var result = HarvestRunner.Shape(
            Spec("id", "price"),
            [Row(("id", ""), ("price", "")), Row(("id", ""), ("price", ""))],
            ["id", "price"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("every column came back empty"));
            Assert.That(result.MatchedRows, Is.EqualTo(2), "the count is still reported, to aid the diagnosis");
        });
    }

    /// <summary>One empty column out of several is a partial read, not a failure — it is reported
    /// and kept, because the columns that did fill are usually the ones being looped over.</summary>
    [Test]
    public void OneEmptyColumnAmongSeveralIsKeptAndReported()
    {
        var result = HarvestRunner.Shape(
            Spec("id", "badge"),
            [Row(("id", "a"), ("badge", "")), Row(("id", "b"), ("badge", ""))],
            ["badge"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Rows, Has.Count.EqualTo(2));
            Assert.That(result.EmptyFields, Does.Contain("badge"));
        });
    }

    [Test]
    public void NoMatchesIsAFailure()
    {
        var result = HarvestRunner.Shape(Spec("id"), [], []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("no rows matched"));
        });
    }

    [Test]
    public void DeduplicationKeepsTheFirstRowForEachKey()
    {
        var spec = Spec("id", "price");
        spec.DedupeBy = "id";

        var result = HarvestRunner.Shape(
            spec,
            [
                Row(("id", "B01"), ("price", "10")),
                Row(("id", "B02"), ("price", "20")),
                Row(("id", "B01"), ("price", "99")),
            ],
            []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Rows.Select(r => r["id"]), Is.EqualTo(new[] { "B01", "B02" }));
            Assert.That(result.Rows[0]["price"], Is.EqualTo("10"), "the first sighting wins");
            Assert.That(result.MatchedRows, Is.EqualTo(3), "what the page held is reported separately from what was kept");
        });
    }

    /// <summary>
    /// Two rows whose key failed to read are two unread rows, not duplicates of each other.
    /// Collapsing them would quietly turn a broken field selector into a shorter dataset.
    /// </summary>
    [Test]
    public void RowsWithABlankDeduplicationKeyAreAllKept()
    {
        var spec = Spec("id");
        spec.DedupeBy = "id";

        var result = HarvestRunner.Shape(
            spec,
            [Row(("id", "")), Row(("id", "")), Row(("id", "B01"))],
            []);

        Assert.That(result.Rows, Has.Count.EqualTo(3));
    }

    [Test]
    public void DeduplicatingByAColumnTheHarvestDoesNotHaveIsRefused()
    {
        var spec = Spec("id");
        spec.DedupeBy = "sku";

        var result = HarvestRunner.Shape(spec, [Row(("id", "a"))], []);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error, Does.Contain("no such column"));
        });
    }

    [Test]
    public void TheRowCapAppliesAfterDeduplication()
    {
        var spec = Spec("id");
        spec.DedupeBy = "id";
        spec.MaxRows = 2;

        var result = HarvestRunner.Shape(
            spec,
            [Row(("id", "a")), Row(("id", "a")), Row(("id", "b")), Row(("id", "c"))],
            []);

        Assert.That(result.Rows.Select(r => r["id"]), Is.EqualTo(new[] { "a", "b" }),
            "capping before de-duplication would have thrown away 'b' to keep a duplicate 'a'");
    }
}

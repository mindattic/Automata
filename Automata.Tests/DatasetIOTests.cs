using System.IO;
using Automata.Core.Automation.Data;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class DatasetIOTests
{
    private string dir = null!;

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private string Path_(string name) => Path.Combine(dir, name);

    [Test]
    public void ReadCsv_ParsesAHeaderAndRows()
    {
        var rows = DatasetIO.ReadCsvText("sku,price\nWT-100,19.99\nWT-200,24.50\n");

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[0]["sku"], Is.EqualTo("WT-100"));
        Assert.That(rows[1]["price"], Is.EqualTo("24.50"));
    }

    [Test]
    public void ReadCsv_HandlesQuotedCommasQuotesAndNewlines()
    {
        var rows = DatasetIO.ReadCsvText("name,note\n\"Smith, John\",\"He said \"\"hi\"\"\"\n\"multi\nline\",plain\n");

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0]["name"], Is.EqualTo("Smith, John"));
            Assert.That(rows[0]["note"], Is.EqualTo("He said \"hi\""));
            Assert.That(rows[1]["name"], Is.EqualTo("multi\nline"));
            Assert.That(rows[1]["note"], Is.EqualTo("plain"));
        });
    }

    [Test]
    public void ReadCsv_ToleratesCrLfAndAMissingTrailingNewline()
    {
        var rows = DatasetIO.ReadCsvText("a,b\r\n1,2\r\n3,4");

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows[1]["b"], Is.EqualTo("4"));
    }

    [Test]
    public void ReadCsv_ShortRowsFillMissingColumnsWithBlanks()
    {
        var rows = DatasetIO.ReadCsvText("a,b,c\n1,2\n");

        Assert.That(rows[0]["c"], Is.EqualTo(""));
    }

    [Test]
    public void WriteThenReadCsv_RoundTripsAwkwardValues()
    {
        var path = Path_("out.csv");
        DatasetIO.WriteCsv(path, [
            new Dictionary<string, string> { ["name"] = "Smith, John", ["note"] = "said \"hi\"" },
            new Dictionary<string, string> { ["name"] = "two\nlines", ["note"] = "" },
        ]);

        var back = DatasetIO.ReadCsv(path);

        Assert.Multiple(() =>
        {
            Assert.That(back, Has.Count.EqualTo(2));
            Assert.That(back[0]["name"], Is.EqualTo("Smith, John"));
            Assert.That(back[0]["note"], Is.EqualTo("said \"hi\""));
            Assert.That(back[1]["name"], Is.EqualTo("two\nlines"));
        });
    }

    [Test]
    public void AppendCsv_AddsRowsWithoutRepeatingTheHeader()
    {
        var path = Path_("out.csv");
        DatasetIO.WriteCsv(path, [new Dictionary<string, string> { ["sku"] = "A" }]);
        DatasetIO.WriteCsv(path, [new Dictionary<string, string> { ["sku"] = "B" }], append: true);

        var text = File.ReadAllText(path);
        var back = DatasetIO.ReadCsv(path);

        Assert.Multiple(() =>
        {
            Assert.That(back.Select(r => r["sku"]), Is.EqualTo(new[] { "A", "B" }));
            Assert.That(text.Split('\n').Count(l => l.StartsWith("sku")), Is.EqualTo(1),
                "the header must appear exactly once");
        });
    }

    /// <summary>
    /// Appending a row with a column the file has never seen rewrites against the union rather
    /// than dropping it — silently losing a value would be the worst of the three options.
    /// </summary>
    [Test]
    public void AppendCsv_WithANewColumn_RewritesAgainstTheUnionInsteadOfDroppingIt()
    {
        var path = Path_("out.csv");
        DatasetIO.WriteCsv(path, [new Dictionary<string, string> { ["sku"] = "A" }]);
        DatasetIO.WriteCsv(path, [new Dictionary<string, string> { ["sku"] = "B", ["price"] = "9.99" }], append: true);

        var back = DatasetIO.ReadCsv(path);

        Assert.Multiple(() =>
        {
            Assert.That(back, Has.Count.EqualTo(2));
            Assert.That(back[0]["sku"], Is.EqualTo("A"));
            Assert.That(back[0]["price"], Is.EqualTo(""), "the earlier row keeps its place, blank in the new column");
            Assert.That(back[1]["price"], Is.EqualTo("9.99"));
        });
    }

    [Test]
    public void Columns_ReadsTheHeaderForThePicker()
    {
        var path = Path_("in.csv");
        File.WriteAllText(path, "sku,price,\"odd, name\"\n1,2,3\n");

        Assert.That(DatasetIO.Columns(path), Is.EqualTo(new[] { "sku", "price", "odd, name" }));
    }

    [Test]
    public void Columns_OfAMissingFileIsEmptyRatherThanThrowing()
    {
        Assert.That(DatasetIO.Columns(Path_("nope.csv")), Is.Empty);
    }

    [Test]
    public void ReadJsonArray_StringifiesScalarsAndKeepsNestedValuesAsJson()
    {
        var path = Path_("in.json");
        File.WriteAllText(path, """
            [ { "sku": "A", "price": 9.99, "active": true, "meta": { "x": 1 }, "missing": null } ]
            """);

        var rows = DatasetIO.ReadJsonArray(path);

        Assert.Multiple(() =>
        {
            Assert.That(rows[0]["sku"], Is.EqualTo("A"));
            Assert.That(rows[0]["price"], Is.EqualTo("9.99"));
            Assert.That(rows[0]["active"], Is.EqualTo("True").Or.EqualTo("true"));
            Assert.That(rows[0]["meta"], Does.Contain("\"x\""), "nested values keep their JSON rather than being lost");
            Assert.That(rows[0]["missing"], Is.EqualTo(""));
        });
    }

    /// <summary>
    /// The reachable half: a field inside a nested object is a column in its own right, named the
    /// way a binding writes it. The parent keeps its JSON too, so nothing that already bound to it
    /// changes meaning.
    /// </summary>
    [Test]
    public void Read_MakesANestedFieldReachableAsAColumn()
    {
        var path = Path_("nested.json");
        File.WriteAllText(path, """
            [ { "sku": "A", "address": { "city": "Ely", "geo": { "lat": "52.4" } },
                "tags": [ "x", "y" ] } ]
            """);

        var row = DatasetIO.Read(path)[0];

        Assert.Multiple(() =>
        {
            Assert.That(row["address.city"], Is.EqualTo("Ely"));
            Assert.That(row["address.geo.lat"], Is.EqualTo("52.4"), "and all the way down");
            Assert.That(row["address"], Does.Contain("\"city\""), "the parent still carries its JSON");
            Assert.That(row.ContainsKey("tags.0"), Is.False,
                "an array keeps its JSON — its length varies per row, so indexed columns would be ragged by construction");
        });
    }

    /// <summary>What the file says is data; what the reader works out is convenience, and data
    /// wins — the same rule a column called "#" gets against a row's position.</summary>
    [Test]
    public void Read_ARealColumnBeatsANestedFieldOfTheSameName()
    {
        var path = Path_("collide.json");
        File.WriteAllText(path, """
            [ { "address": { "city": "Ely" }, "address.city": "the real one" } ]
            """);

        Assert.That(DatasetIO.Read(path)[0]["address.city"], Is.EqualTo("the real one"));
    }

    /// <summary>The picker offers what a binding can reach, or a nested field is one nobody
    /// finds.</summary>
    [Test]
    public void Columns_OffersNestedFieldsToo()
    {
        var path = Path_("nested-columns.json");
        File.WriteAllText(path, """
            [ { "sku": "A", "address": { "city": "Ely" } } ]
            """);

        Assert.That(DatasetIO.Columns(path), Is.EqualTo(new[] { "sku", "address", "address.city" }));
    }

    /// <summary>
    /// An append rewrites every row it read. Reading the convenience columns and writing them back
    /// would bake them into the file, and the next append would do it again.
    /// </summary>
    [Test]
    public void AppendingToAJsonDatasetDoesNotWriteTheFlattenedColumnsIntoIt()
    {
        var path = Path_("append-nested.json");
        File.WriteAllText(path, """
            [ { "address": { "city": "Ely" } } ]
            """);

        DatasetIO.Write(path, [new Dictionary<string, string> { ["sku"] = "B" }], append: true);

        Assert.That(File.ReadAllText(path), Does.Not.Contain("address.city"));
    }

    [Test]
    public void WriteThenReadJson_RoundTripsAndAppends()
    {
        var path = Path_("out.json");
        DatasetIO.WriteJsonArray(path, [new Dictionary<string, string> { ["a"] = "1" }]);
        DatasetIO.WriteJsonArray(path, [new Dictionary<string, string> { ["a"] = "2" }], append: true);

        Assert.That(DatasetIO.ReadJsonArray(path).Select(r => r["a"]), Is.EqualTo(new[] { "1", "2" }));
    }

    [Test]
    public void Read_PicksTheFormatFromTheExtension()
    {
        var csv = Path_("d.csv");
        var json = Path_("d.json");
        DatasetIO.Write(csv, [new Dictionary<string, string> { ["a"] = "1" }]);
        DatasetIO.Write(json, [new Dictionary<string, string> { ["a"] = "2" }]);

        Assert.That(DatasetIO.Read(csv)[0]["a"], Is.EqualTo("1"));
        Assert.That(DatasetIO.Read(json)[0]["a"], Is.EqualTo("2"));
    }

    [Test]
    public void ReadingAMissingFileIsEmptyRatherThanThrowing()
    {
        Assert.That(DatasetIO.ReadCsv(Path_("nope.csv")), Is.Empty);
        Assert.That(DatasetIO.ReadJsonArray(Path_("nope.json")), Is.Empty);
    }
}

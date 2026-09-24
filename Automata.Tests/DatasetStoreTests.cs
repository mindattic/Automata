using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>Datasets in the database: the same rows a task always saw, and the files they came
/// from and go back out to.</summary>
[TestFixture]
public class DatasetStoreTests
{
    private string workDir = null!;
    private TestDb db = null!;
    private DatasetStore datasets = null!;

    [SetUp]
    public void SetUp()
    {
        db = new TestDb();
        datasets = db.Datasets();
        workDir = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workDir);
    }

    [TearDown]
    public void TearDown()
    {
        db.Dispose();
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
    }

    [Test]
    public void ACsvFileImportsAndExportsBackToTheSameText()
    {
        var file = Path.Combine(workDir, "skus.csv");
        const string text = "sku,note\nA-1,\"has, a comma\"\nB-2,\"says \"\"hi\"\"\"\nC-3,\"two\nlines\"\n";
        File.WriteAllText(file, text);

        var name = datasets.ImportFile(file);

        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("skus.csv"));
            Assert.That(datasets.List(), Is.EqualTo(new[] { "skus.csv" }));
            Assert.That(datasets.Count("skus.csv"), Is.EqualTo(3));
            Assert.That(datasets.Columns("skus.csv"), Is.EqualTo(new[] { "sku", "note" }));
            Assert.That(datasets.Read("skus.csv")[1]["note"], Is.EqualTo("says \"hi\""));
            Assert.That(datasets.ExportText("skus.csv"), Is.EqualTo(text));
        });
    }

    /// <summary>A JSON dataset keeps its nested values as they are — the file store rewrote them
    /// into strings on every append — and still offers their leaves as columns.</summary>
    [Test]
    public void AJsonDatasetKeepsItsNestedValues_AndStillFlattensForBinding()
    {
        datasets.ImportText("roster.json", """[ { "Name": "Ada", "Address": { "City": "London" }, "Tags": ["a", "b"] } ]""");
        datasets.Append("roster.json", new Dictionary<string, string> { ["Name"] = "Grace" });

        var rows = datasets.Read("roster.json");
        var exported = datasets.ExportText("roster.json");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0]["Address.City"], Is.EqualTo("London"));
            Assert.That(rows[0]["Tags"], Is.EqualTo("[\"a\", \"b\"]").Or.EqualTo("[\"a\",\"b\"]"));
            Assert.That(datasets.Columns("roster.json"), Does.Contain("Address.City"));
            Assert.That(exported, Does.Contain("\"City\": \"London\""), "the object is still an object");
        });
    }

    [Test]
    public void AppendingANewColumnWidensTheHeaderAndEarlierRowsReadEmpty()
    {
        datasets.Append("out.csv", new Dictionary<string, string> { ["a"] = "1" });
        datasets.Append("out.csv", new Dictionary<string, string> { ["a"] = "2", ["b"] = "x" });

        var rows = datasets.Read("out.csv");
        Assert.Multiple(() =>
        {
            Assert.That(datasets.Columns("out.csv"), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(rows[0]["b"], Is.EqualTo(""));
            Assert.That(rows[1]["b"], Is.EqualTo("x"));
            Assert.That(datasets.ExportText("out.csv"), Is.EqualTo("a,b\n1,\n2,x\n"));
        });
    }

    [Test]
    public void WritingWithoutAppendReplacesEveryRow()
    {
        datasets.Write("out.csv", [new Dictionary<string, string> { ["a"] = "old" }], append: true);
        datasets.Write("out.csv", [new Dictionary<string, string> { ["b"] = "new" }], append: false);

        Assert.That(datasets.ExportText("out.csv"), Is.EqualTo("b\nnew\n"));
    }

    [Test]
    public void NamesAreCaseInsensitiveAndSanitisedLikeFileNames()
    {
        datasets.ImportText("Prices.csv", "p\n1\n");

        Assert.Multiple(() =>
        {
            Assert.That(datasets.Exists("prices.CSV"), Is.True);
            Assert.That(datasets.Read("PRICES.csv").Single()["p"], Is.EqualTo("1"));
            Assert.That(datasets.ImportText(@"..\..\escape.csv", "x\n1\n"), Is.EqualTo(@".._.._escape.csv"));
        });
    }

    [Test]
    public void ANonArrayJsonFileIsRefused()
    {
        Assert.That(() => datasets.ImportText("bad.json", """{ "not": "an array" }"""),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(datasets.Exists("bad.json"), Is.False);
    }

    [Test]
    public void ExportingToAFileWritesWhatExportTextSays()
    {
        datasets.ImportText("x.csv", "a\n1\n");
        var file = Path.Combine(workDir, "sub", "x.csv");

        datasets.ExportFile("x.csv", file);

        Assert.That(File.ReadAllText(file), Is.EqualTo("a\n1\n"));
    }

    [Test]
    public void AMissingDatasetReadsAsNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(datasets.Exists("nope.csv"), Is.False);
            Assert.That(datasets.Read("nope.csv"), Is.Empty);
            Assert.That(datasets.Columns("nope.csv"), Is.Empty);
            Assert.That(datasets.Count("nope.csv"), Is.Zero);
            Assert.Throws<InvalidOperationException>(() => datasets.ExportText("nope.csv"));
        });
    }
}

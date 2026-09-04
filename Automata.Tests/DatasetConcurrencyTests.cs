using NUnit.Framework;
using Automata.Core.Automation.Storage;

namespace Automata.Tests;

/// <summary>
/// Appending to one dataset from several places at once.
/// <para>
/// This exists because a real parallel run came back short: four browser lanes each appended a
/// price to the same CSV, two rows vanished, and the total was wrong by exactly those two. An
/// append is a read-modify-write — read the rows, work out the union of columns, write it all back
/// — so two writers racing lose whichever update finished first, and on Windows they frequently
/// collide outright on the file handle instead.
/// </para>
/// </summary>
[TestFixture]
public class DatasetConcurrencyTests
{
    private string root = null!;
    private DatasetStore datasets = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        datasets = new DatasetStore(root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static Dictionary<string, string> Row(int i) => new(StringComparer.Ordinal)
    {
        ["sku"] = $"SKU-{i:000}",
        ["price"] = $"{i}.00",
    };

    /// <summary>The shape of the run that found the bug: many writers, one file, one row each.</summary>
    [Test]
    public void EveryConcurrentAppendSurvives()
    {
        const int writers = 24;

        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => datasets.Append("prices.csv", Row(i)));

        var rows = datasets.Read("prices.csv");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(writers), "an append was lost");
            Assert.That(rows.Select(r => r["sku"]).Distinct().Count(), Is.EqualTo(writers),
                "a row was written twice");
        });
    }

    /// <summary>
    /// The nastier variant: a writer whose row introduces a new column rewrites the entire file
    /// rather than appending to it, so it overlaps a plain append for far longer.
    /// </summary>
    [Test]
    public void ConcurrentAppendsSurviveEvenWhenColumnsKeepChanging()
    {
        const int writers = 24;

        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var row = Row(i);
            // Every third row brings a column of its own, forcing a full-file rewrite.
            if (i % 3 == 0) row[$"extra{i}"] = "x";
            datasets.Append("prices.csv", row);
        });

        var rows = datasets.Read("prices.csv");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(writers));
            Assert.That(rows.All(r => r.ContainsKey("sku") && r["sku"].Length > 0), Is.True,
                "a rewrite dropped a row's original columns");
        });
    }

    /// <summary>A reader must never catch a full-file rewrite half-finished.</summary>
    [Test]
    public void ReadingWhileOthersAppendNeverSeesATornFile()
    {
        datasets.Append("prices.csv", Row(0));
        var torn = 0;

        Parallel.Invoke(
            () =>
            {
                for (var i = 1; i < 40; i++)
                {
                    var row = Row(i);
                    if (i % 3 == 0) row[$"extra{i}"] = "x";
                    datasets.Append("prices.csv", row);
                }
            },
            () =>
            {
                for (var i = 0; i < 80; i++)
                {
                    var rows = datasets.Read("prices.csv");
                    if (rows.Any(r => !r.TryGetValue("sku", out var sku) || sku.Length == 0))
                        Interlocked.Increment(ref torn);
                }
            });

        Assert.That(torn, Is.Zero, "a read landed inside a rewrite");
    }

    /// <summary>
    /// "Start fresh each run" under concurrency. The claim decides who replaces and who appends,
    /// and it is settled inside the write lock — decided outside it, the winner would replace a
    /// file the losers had already appended to, and a parallel loop would come back holding only
    /// the rows written after the last replace.
    /// </summary>
    [Test]
    public void StartingFreshUnderConcurrencyClearsOnceAndKeepsEveryRow()
    {
        const int writers = 24;
        datasets.Append("prices.csv", Row(999)); // last run's leftovers

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Claim()
        {
            lock (claimed) return claimed.Add("prices.csv");
        }

        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => datasets.Write("prices.csv", [Row(i)], append: true, claimFirstWrite: Claim));

        var rows = datasets.Read("prices.csv");
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(writers), "a row was lost to the reset");
            Assert.That(rows.Any(r => r["sku"] == "SKU-999"), Is.False,
                "the previous run's rows survived, so nothing was cleared");
        });
    }

    /// <summary>
    /// The lock has to be per file, or every dataset in the workspace would queue behind whichever
    /// one is busiest — and a parallel run writing two datasets would serialise for no reason.
    /// </summary>
    [Test]
    public void TwoDifferentDatasetsDoNotBlockEachOther()
    {
        using var first = ExclusiveFileLock.Acquire(datasets.PathFor("a.csv"));

        Assert.DoesNotThrow(() =>
        {
            using var second = ExclusiveFileLock.Acquire(
                datasets.PathFor("b.csv"), TimeSpan.FromMilliseconds(500));
        });
    }

    [Test]
    public void WaitingTooLongForTheSameFileSaysSoRatherThanWritingAnyway()
    {
        using var held = ExclusiveFileLock.Acquire(datasets.PathFor("a.csv"));

        var thrown = Assert.Throws<IOException>(() =>
        {
            using var _ = ExclusiveFileLock.Acquire(
                datasets.PathFor("a.csv"), TimeSpan.FromMilliseconds(200));
        });
        Assert.That(thrown!.Message, Does.Contain("a.csv"));
    }
}

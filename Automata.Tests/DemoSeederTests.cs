using NUnit.Framework;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;

namespace Automata.Tests;

/// <summary>
/// The rules that decide what regenerating the examples is allowed to touch. The whole point of
/// the demo batch is that a new user can run something that works before building their own, and
/// the whole risk is that regenerating it eats work somebody did on top of one.
/// </summary>
[TestFixture]
public class DemoSeederTests
{
    private string root = null!;
    private CollectionStore collections = null!;
    private DemoSeeder seeder = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"));
        collections = new CollectionStore(Path.Combine(root, "collections"));
        seeder = new DemoSeeder(collections, Path.Combine(root, "demos"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static DemoStatus Status(IReadOnlyList<DemoStatus> all, string key) =>
        all.First(s => s.Key == key);

    private TaskDefinition Task(string key) =>
        collections.GetTask(Status(seeder.Survey(), key).TaskId!)!;

    private const string Key = "shop-prices-sequential";

    // ---- first load -------------------------------------------------------------------------

    [Test]
    public void BeforeAnythingIsSeededEveryDemoIsMissing()
    {
        Assert.That(seeder.Survey().Select(s => s.State), Is.All.EqualTo(DemoState.Missing));
    }

    [Test]
    public void SeedingWritesThePagesAndTheTasks()
    {
        var report = seeder.SeedMissing();

        Assert.Multiple(() =>
        {
            Assert.That(report.Added, Is.Not.Empty);
            Assert.That(report.PagesWritten, Does.Contain("shop/search.html"));
            Assert.That(File.Exists(Path.Combine(seeder.RootPath, "shop", "search.html")), Is.True);
            Assert.That(seeder.Survey().Select(s => s.State), Is.All.EqualTo(DemoState.Current));
        });
    }

    /// <summary>
    /// Startup calls this on every launch, so it has to be a no-op the second time. A generator
    /// that re-added its examples each run would fill the sidebar with copies.
    /// </summary>
    [Test]
    public void SeedingTwiceAddsNothingTheSecondTime()
    {
        seeder.SeedMissing();
        var again = seeder.SeedMissing();

        Assert.Multiple(() =>
        {
            Assert.That(again.Added, Is.Empty);
            Assert.That(again.Refreshed, Is.Empty);
            Assert.That(again.Reverted, Is.Empty);
            Assert.That(again.Cloned, Is.Empty);
        });
    }

    /// <summary>Pages are generated output with nothing of the user's in them, so they are
    /// rewritten every time — a stale page under a fresh task fails for reasons that teach
    /// nobody anything.</summary>
    [Test]
    public void SeedingRestoresAPageSomebodyDeleted()
    {
        seeder.SeedMissing();
        var page = Path.Combine(seeder.RootPath, "buttons.html");
        File.Delete(page);

        seeder.SeedMissing();

        Assert.That(File.Exists(page), Is.True);
    }

    // ---- detecting a hand edit --------------------------------------------------------------

    [Test]
    public void ChangingADemosStepsMarksItEdited()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Add(new Step { Action = StepAction.Click, Label = "mine" });
        collections.SaveTask(task);

        Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Edited));
    }

    /// <summary>Renaming a demo is not editing it. Nagging about a retitled example — and, worse,
    /// renaming it back — would both be wrong.</summary>
    [Test]
    public void RenamingADemoDoesNotCountAsEditingIt()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Name = "My price run";
        collections.SaveTask(task);

        Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Current));
    }

    // ---- regenerating over an edit ----------------------------------------------------------

    [Test]
    public void RegeneratingLeavesAnEditedDemoAloneWhenNoChoiceIsGiven()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);

        var report = seeder.Regenerate(new Dictionary<string, DemoResolution>());

        Assert.Multiple(() =>
        {
            Assert.That(report.Kept, Is.Not.Empty);
            Assert.That(report.Reverted, Is.Empty);
            Assert.That(collections.GetTask(task.Id)!.Steps, Is.Empty, "their version is untouched");
        });
    }

    [Test]
    public void RevertingRestoresTheFactoryStepsInPlace()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        var factoryStepCount = task.Steps.Count;
        task.Steps.Clear();
        collections.SaveTask(task);

        seeder.Regenerate(new Dictionary<string, DemoResolution> { [Key] = DemoResolution.Revert });

        var after = collections.GetTask(task.Id)!;
        Assert.Multiple(() =>
        {
            Assert.That(after.Steps, Has.Count.EqualTo(factoryStepCount));
            Assert.That(after.Id, Is.EqualTo(task.Id), "reverting is in place, not a replacement");
            Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Current));
        });
    }

    /// <summary>
    /// Cloning is the option that has to lose nothing: their work survives verbatim, the pristine
    /// copy shows up beside it, and nothing is left in a state that will ask again next time.
    /// </summary>
    [Test]
    public void CloningKeepsTheirVersionAndAddsAPristineOneBesideIt()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        var collectionId = task.CollectionId;
        task.Name = "My price run";
        task.Steps.Clear();
        collections.SaveTask(task);

        var report = seeder.Regenerate(
            new Dictionary<string, DemoResolution> { [Key] = DemoResolution.Clone });

        var all = collections.LoadTasks(collectionId);
        var theirs = all.First(t => t.Id == task.Id);
        var clone = all.First(t => t.Demo?.Key == Key);

        Assert.Multiple(() =>
        {
            Assert.That(report.Cloned, Is.Not.Empty);
            Assert.That(theirs.Steps, Is.Empty, "their edit survives exactly as it was");
            Assert.That(theirs.Name, Is.EqualTo("My price run"), "and keeps its name");
            Assert.That(theirs.Demo, Is.Null, "it is their task now, not a tracked copy of ours");
            Assert.That(clone.Id, Is.Not.EqualTo(task.Id));
            Assert.That(clone.Steps, Is.Not.Empty);
        });
    }

    /// <summary>
    /// Only one task may carry a demo key, or a later survey would report on whichever it happened
    /// to find first — and only one task may carry a name, because the name is the file name.
    /// </summary>
    [Test]
    public void CloningLeavesExactlyOneTrackedDemoAndNoDuplicateNames()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);

        seeder.Regenerate(new Dictionary<string, DemoResolution> { [Key] = DemoResolution.Clone });

        var all = collections.LoadTasks(task.CollectionId);
        Assert.Multiple(() =>
        {
            Assert.That(all.Count(t => t.Demo?.Key == Key), Is.EqualTo(1));
            Assert.That(all.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(all.Count));
            Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Current),
                "nothing is left for the next regenerate to ask about");
        });
    }

    /// <summary>Regenerating right after a clone must be quiet — otherwise the clone piles up a
    /// copy on every run.</summary>
    [Test]
    public void RegeneratingAfterACloneAddsNothingFurther()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);
        seeder.Regenerate(new Dictionary<string, DemoResolution> { [Key] = DemoResolution.Clone });

        var again = seeder.Regenerate(new Dictionary<string, DemoResolution>());

        Assert.Multiple(() =>
        {
            Assert.That(again.Cloned, Is.Empty);
            Assert.That(again.Added, Is.Empty);
            Assert.That(again.Kept, Is.Empty);
        });
    }
}

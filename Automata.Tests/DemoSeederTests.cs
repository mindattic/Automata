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
            Assert.That(again.Restored, Is.Empty);
            Assert.That(again.Kept, Is.Empty);
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

    /// <summary>
    /// A rename counts as an edit, because regenerating puts the name back with everything else.
    /// A survey that called a renamed example "up to date" would be promising not to touch
    /// something it is about to rename.
    /// </summary>
    [Test]
    public void RenamingADemoCountsAsEditingIt()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Name = "My price run";
        collections.SaveTask(task);

        Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Edited));
    }

    // ---- regenerating ------------------------------------------------------------------------

    /// <summary>
    /// The rule the whole Demos collection rests on: <b>regenerating restores everything</b>. It
    /// is not a negotiation, because a batch where any given example might be somebody's
    /// half-finished experiment cannot be the place a new user looks for a working reference.
    /// </summary>
    [Test]
    public void RegeneratingRestoresAnEditedDemoWithoutBeingAsked()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        var factoryStepCount = task.Steps.Count;
        task.Steps.Clear();
        collections.SaveTask(task);

        var report = seeder.Regenerate();

        var after = collections.GetTask(DemoTask.TaskIdFor(Key))!;
        Assert.Multiple(() =>
        {
            Assert.That(report.Restored, Is.Not.Empty);
            Assert.That(report.Kept, Is.Empty, "nothing is left behind for a second prompt");
            Assert.That(after.Steps, Has.Count.EqualTo(factoryStepCount));
            Assert.That(Status(seeder.Survey(), Key).State, Is.EqualTo(DemoState.Current));
        });
    }

    /// <summary>The name goes back too — a renamed example is no easier to recognise than a
    /// rewritten one, and "restored to the shipped version" has to mean all of it.</summary>
    [Test]
    public void RegeneratingRestoresTheNameAsWellAsTheSteps()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        var factoryName = task.Name;
        task.Name = "My price run";
        collections.SaveTask(task);

        seeder.Regenerate();

        var all = collections.LoadTasks(task.CollectionId);
        Assert.Multiple(() =>
        {
            Assert.That(all.Count(t => t.Demo?.Key == Key), Is.EqualTo(1));
            Assert.That(all.First(t => t.Demo?.Key == Key).Name, Is.EqualTo(factoryName));
            Assert.That(all.Any(t => t.Name == "My price run"), Is.False,
                "their renamed copy is not left behind as a second task");
        });
    }

    /// <summary>
    /// Startup is the other half of the rule. It happens without anyone asking, so it may not
    /// destroy work — and the difference between the two paths is the whole reason regenerating
    /// can be as blunt as it is.
    /// </summary>
    [Test]
    public void SeedingOnLaunchStillLeavesAnEditedDemoExactlyAsItIs()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);

        var report = seeder.SeedMissing();

        Assert.Multiple(() =>
        {
            Assert.That(report.Kept, Is.Not.Empty);
            Assert.That(report.Restored, Is.Empty);
            Assert.That(collections.GetTask(task.Id)!.Steps, Is.Empty, "their version is untouched");
        });
    }

    /// <summary>
    /// The way to keep a modified example: move it out. It stops being an example on the way —
    /// marker and fixed id both — or the generator would write its replacement onto the same id.
    /// </summary>
    [Test]
    public void MovingAnEditedDemoOutOfDemosKeepsItSafeFromRegenerating()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);
        var mine = collections.CreateCollection("Mine");

        var moved = collections.MoveTask(task.Id, mine.Id);
        seeder.Regenerate();

        var theirs = collections.LoadTasks(mine.Id).Single();
        Assert.Multiple(() =>
        {
            Assert.That(theirs.Steps, Is.Empty, "their version survived the regenerate untouched");
            Assert.That(theirs.Demo, Is.Null, "it stopped being an example when it left");
            Assert.That(theirs.Id, Is.Not.EqualTo(DemoTask.TaskIdFor(Key)),
                "and gave the example id back, or two tasks would answer to it");
            Assert.That(moved.Id, Is.EqualTo(theirs.Id));
            Assert.That(collections.GetTask(DemoTask.TaskIdFor(Key))!.Steps, Is.Not.Empty,
                "the pristine example is back in Demos");
        });
    }

    /// <summary>Duplicating is the other way, for someone who wants both. The copy is not an
    /// example — two tasks answering to one key would leave the generator restoring whichever it
    /// found first and silently leaving the other behind.</summary>
    [Test]
    public void DuplicatingADemoLeavesTheCopyOutsideTheGeneratorsReach()
    {
        seeder.SeedMissing();
        var task = Task(Key);

        var copy = collections.DuplicateTask(task.Id);

        Assert.Multiple(() =>
        {
            Assert.That(copy.Demo, Is.Null);
            Assert.That(copy.Id, Is.Not.EqualTo(task.Id));
            Assert.That(seeder.Survey().Count(st => st.Key == Key), Is.EqualTo(1),
                "the copy must not show up as a second claimant of the key");
        });
    }

    /// <summary>Regenerating right after a regenerate must be quiet, or the dialog would keep
    /// reporting work to do on a collection it has just finished rebuilding.</summary>
    [Test]
    public void RegeneratingTwiceReportsNothingTheSecondTime()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        task.Steps.Clear();
        collections.SaveTask(task);
        seeder.Regenerate();

        var again = seeder.Regenerate();

        Assert.Multiple(() =>
        {
            Assert.That(again.Added, Is.Empty);
            Assert.That(again.Refreshed, Is.Empty);
            Assert.That(again.Restored, Is.Empty);
            Assert.That(again.Kept, Is.Empty);
        });
    }

    // ---- identity ----------------------------------------------------------------------------

    /// <summary>
    /// A demo's id is part of the demo, not something the store decides — which is what lets one
    /// example call another by name in the editor and by id on disk.
    /// </summary>
    [Test]
    public void EverySeededDemoCarriesItsFixedId()
    {
        seeder.SeedMissing();

        foreach (var status in seeder.Survey())
            Assert.That(status.TaskId, Is.EqualTo(DemoTask.TaskIdFor(status.Key)), status.Name);
    }

    /// <summary>Every runTask step in the batch resolves — the reason the ids are fixed at all.</summary>
    [Test]
    public void ExamplesThatCallOtherExamplesResolve()
    {
        seeder.SeedMissing();
        var collectionId = collections.LoadCollections().First(c => c.Name == DemoTasks.CollectionName).Id;
        var calls = collections.LoadTasks(collectionId)
            .SelectMany(t => t.Steps)
            .Where(s => s.Action == StepAction.RunTask)
            .ToList();

        Assert.That(calls, Is.Not.Empty, "no example calls another, so nothing proves the ids work");
        foreach (var call in calls)
            Assert.That(collections.GetTask(call.RunTaskId!), Is.Not.Null, call.Label);
    }

    /// <summary>
    /// An install seeded before demo ids were fixed carries a generated one. The store keys a task
    /// file by the id inside it, so writing the fixed id straight over that file would read as a
    /// different task landing on an occupied name — and the store would keep BOTH, leaving a
    /// "(2)" copy behind and two tasks claiming the same demo.
    /// </summary>
    [Test]
    public void ADemoSeededWithAGeneratedIdIsReplacedRatherThanDuplicated()
    {
        seeder.SeedMissing();
        var task = Task(Key);
        var collectionId = task.CollectionId;
        var name = task.Name;

        collections.DeleteTask(task.Id);
        task.Id = Guid.NewGuid().ToString("n");
        task.Steps.Clear();
        collections.SaveTask(task);

        seeder.Regenerate();

        var all = collections.LoadTasks(collectionId);
        Assert.Multiple(() =>
        {
            Assert.That(all.Count(t => t.Demo?.Key == Key), Is.EqualTo(1), "one task claims the demo");
            Assert.That(all.Count(t => t.Name == name), Is.EqualTo(1), "and nothing was left behind as a copy");
            Assert.That(all.First(t => t.Demo?.Key == Key).Id, Is.EqualTo(DemoTask.TaskIdFor(Key)));
        });
    }

}

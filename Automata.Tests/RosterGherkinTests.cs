using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Flow;
using Automata.Core.Automation.Model;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// The roster example is the one that answers "a JSON blob from a previous task, iterated and
/// branched on". It has to be right in three places at once — the step tree that runs, the Gherkin
/// a person reads, and the compile back from that Gherkin — so these check them against each other
/// rather than any one in isolation.
/// </summary>
[TestFixture]
public class RosterGherkinTests
{
    private static TaskDefinition Roster()
    {
        var factory = DemoTasks.All(Path.Combine(Path.GetTempPath(), "automata-roster"))
            .First(d => d.Key == "roster");
        return new TaskDefinition
        {
            Id = factory.TaskId,
            Name = factory.Name,
            Description = factory.Description,
            StartUrl = factory.StartUrl,
            Steps = factory.Steps,
        };
    }

    private static GherkinWriteResult Render() =>
        GherkinWriter.Write(new Collection { Name = "Demos" }, [Roster()]);

    /// <summary>
    /// The whole branch reads back in order: the guard, the inner guard, what it does, the word
    /// that turns the corner, and what happens instead.
    /// </summary>
    [Test]
    public void TheWholeBranchIsThereToRead()
    {
        var text = Render().Text;

        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("row.Role is present"), text);
            Assert.That(text, Does.Contain("row.Name is not present"), text);
            Assert.That(text, Does.Contain("I click \"#skip\""), text);
            Assert.That(text, Does.Contain("But otherwise"), text);
            Assert.That(text, Does.Contain("I set \"#txtName\" to \"<Name>\""), text);
        });
    }

    /// <summary>
    /// A guard is a step, so its position among the others is the whole meaning — the skip has to
    /// sit before the word that turns the corner and the typing after it, or the feature says the
    /// opposite of what the task does.
    /// </summary>
    [Test]
    public void AndInTheOrderThatMeansWhatTheTaskDoes()
    {
        var lines = Render().Text.Split('\n').Select(l => l.Trim()).ToList();

        var guard = lines.FindIndex(l => l.Contains("row.Name is not present"));
        var skip = lines.FindIndex(l => l.Contains("I click \"#skip\""));
        var otherwise = lines.FindIndex(l => l == "But otherwise");
        var type = lines.FindIndex(l => l.Contains("I set \"#txtName\""));

        Assert.That(guard, Is.LessThan(skip));
        Assert.That(skip, Is.LessThan(otherwise));
        Assert.That(otherwise, Is.LessThan(type));
    }

    /// <summary>
    /// And it says what it could NOT express.
    /// <para>
    /// This task loops AND does something once afterwards, and a Scenario Outline has no room for
    /// the second part — every step in an outline runs per row. So it renders as a plain Scenario
    /// with the loop as a comment, which is honest but lossy, and the writer has to say so rather
    /// than let someone recompile it and lose the loop. The clean-outline round-trip is proven
    /// separately, on the same guard shape, in <see cref="GherkinFlowTests"/>.
    /// </para>
    /// </summary>
    [Test]
    public void AndSaysWhatItCouldNotExpress()
    {
        var written = Render();

        Assert.That(written.IsLossy, Is.True);
        Assert.That(string.Join(" | ", written.Reasons), Does.Contain("ForEach"));
        Assert.That(written.Reasons.Any(r => r.Contains("lose their nesting")), Is.True,
            "the steps inside the loop are shown flat, and that has to be said — they used to be "
            + "dropped in silence");
    }
}

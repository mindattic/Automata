using NUnit.Framework;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Model;

namespace Automata.Tests;

/// <summary>
/// The generated examples are the only place a capability can be seen working, so this fixture
/// makes them the definition of done: <b>a new StepAction, WaitMode or ConditionOp fails the build
/// until some seeded example demonstrates it.</b>
/// <para>
/// The same mechanical trick as the sidebar's floor check, aimed at a different failure. A
/// capability with no example is a capability nobody finds — it ships, it is never used, and it
/// rots until the day someone tries it. Making the compiler's own enum the checklist means the
/// example is written while the feature is still in someone's head.
/// </para>
/// <para>
/// <see cref="NotDemonstrable"/> is the escape hatch, and it is deliberately awkward: adding to it
/// means writing down, in code review, why a thing this product offers cannot be shown working.
/// </para>
/// </summary>
[TestFixture]
public class DemoCoverageTests
{
    /// <summary>
    /// Values no seeded example can honestly exercise, each with the reason.
    /// <para>
    /// <see cref="WaitMode.UntilSignal"/>: the engine refuses it outright ("waiting for a signal
    /// needs the scheduler, which is not built yet"), and nothing in the product produces a signal.
    /// An example of it would be an example that hangs. Delete this entry the day signals land.
    /// </para>
    /// </summary>
    private static readonly HashSet<object> NotDemonstrable = [WaitMode.UntilSignal];

    /// <summary>The demo root only decides what the baked-in URLs point at, not the shape.</summary>
    private static IReadOnlyList<DemoTask> Demos() =>
        DemoTasks.All(Path.Combine(Path.GetTempPath(), "automata-coverage"));

    private static IEnumerable<Step> AllSteps()
    {
        foreach (var demo in Demos())
            foreach (var step in Walk(demo.Steps))
                yield return step;
    }

    private static IEnumerable<Step> Walk(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Walk(step.Children)) yield return child;
        }
    }

    // ---- the coverage rules -------------------------------------------------------------------

    [Test]
    public void EveryStepActionIsDemonstratedBySomeExample()
    {
        AssertCovered<StepAction>(AllSteps().Select(s => (object)s.Action));
    }

    [Test]
    public void EveryWaitModeIsDemonstratedBySomeExample()
    {
        AssertCovered<WaitMode>(AllSteps().Where(s => s.Wait != null).Select(s => (object)s.Wait!.Mode));
    }

    /// <summary>Conditions reach the model two ways — an <c>if</c> and a wait on a condition — and
    /// both count, because both are places the picker offers the whole operator list.</summary>
    [Test]
    public void EveryConditionOpIsDemonstratedBySomeExample()
    {
        var ops = AllSteps()
            .SelectMany(s => new[] { s.Condition, s.Wait?.Condition })
            .Where(c => c != null)
            .Select(c => (object)c!.Op);
        AssertCovered<ConditionOp>(ops);
    }

    /// <summary>
    /// An exemption that has quietly become false is worse than no exemption: it reads like a
    /// standing limitation of the product when the limitation is gone.
    /// </summary>
    [Test]
    public void NothingIsExemptedThatIsAlreadyDemonstrated()
    {
        var covered = new HashSet<object>(
            AllSteps().Select(s => (object)s.Action)
                .Concat(AllSteps().Where(s => s.Wait != null).Select(s => (object)s.Wait!.Mode))
                .Concat(AllSteps()
                    .SelectMany(s => new[] { s.Condition, s.Wait?.Condition })
                    .Where(c => c != null)
                    .Select(c => (object)c!.Op)));

        Assert.That(NotDemonstrable.Where(covered.Contains), Is.Empty,
            "an example now demonstrates this, so its exemption is stale — delete it");
    }

    private static void AssertCovered<T>(IEnumerable<object> used) where T : struct, Enum
    {
        var seen = used.ToHashSet();
        var missing = Enum.GetValues<T>()
            .Cast<object>()
            .Where(v => !seen.Contains(v) && !NotDemonstrable.Contains(v))
            .ToList();

        Assert.That(missing, Is.Empty,
            $"no seeded example uses {typeof(T).Name}.{string.Join("/", missing)} — add one to " +
            "DemoTasks (with a page in DemoPages if it needs one), or, if it truly cannot be " +
            "shown working, add it to DemoCoverageTests.NotDemonstrable with the reason.");
    }

    // ---- the examples have to hold together too -------------------------------------------------

    /// <summary>
    /// A demo's id is fixed so a <c>runTask</c> step can name it. Two demos sharing one would make
    /// the store give one of them a fresh id on load, silently breaking whichever reference lost.
    /// </summary>
    [Test]
    public void EveryDemoHasItsOwnKeyNameAndId()
    {
        var demos = Demos();
        Assert.Multiple(() =>
        {
            Assert.That(demos.Select(d => d.Key).Distinct().Count(), Is.EqualTo(demos.Count), "keys");
            Assert.That(demos.Select(d => d.Name).Distinct().Count(), Is.EqualTo(demos.Count), "names");
            Assert.That(demos.Select(d => d.TaskId).Distinct().Count(), Is.EqualTo(demos.Count), "ids");
        });
    }

    /// <summary>Step ids are what bindings point at; a duplicate would silently redirect one.</summary>
    [Test]
    public void EveryStepIdAcrossEveryExampleIsUnique()
    {
        var ids = AllSteps().Select(s => s.Id).ToList();
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
            "duplicate step id(s): " +
            string.Join(", ", ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)));
    }

    /// <summary>A runTask step pointing at nothing fails only when someone runs it.</summary>
    [Test]
    public void EveryRunTaskStepNamesAnExampleThatExists()
    {
        var ids = Demos().Select(d => d.TaskId).ToHashSet(StringComparer.Ordinal);
        var dangling = AllSteps()
            .Where(s => s.Action == StepAction.RunTask)
            .Where(s => s.RunTaskId == null || !ids.Contains(s.RunTaskId))
            .Select(s => $"{s.Label} -> {s.RunTaskId ?? "(none)"}")
            .ToList();

        Assert.That(dangling, Is.Empty);
    }

    /// <summary>
    /// Every binding to a step output has to name a step that declares it, and one that runs
    /// earlier — the engine reports an unresolved binding as a step failure, which in a demo reads
    /// as the product being broken.
    /// </summary>
    [Test]
    public void EveryStepOutputBindingNamesAnEarlierStepThatPublishesIt()
    {
        foreach (var demo in Demos())
        {
            var published = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in Walk(demo.Steps))
            {
                foreach (var binding in BindingsOf(step))
                {
                    if (binding.Kind != BindingKind.StepOutput) continue;
                    var key = $"{binding.SourceStepId}/{binding.OutputField}";
                    Assert.That(published, Does.Contain(key),
                        $"'{demo.Name}' step '{step.Label}' binds to {key}, which no earlier step publishes");
                }

                foreach (var output in step.Outputs ?? [])
                    published.Add($"{step.Id}/{output.Name}");
            }
        }
    }

    private static IEnumerable<BindingRef> BindingsOf(Step step)
    {
        foreach (var binding in step.Bindings?.Values ?? Enumerable.Empty<BindingRef>())
            if (binding != null) yield return binding;

        foreach (var binding in step.WriteDataset?.Columns.Values ?? Enumerable.Empty<BindingRef>())
            if (binding != null) yield return binding;

        foreach (var condition in new[] { step.Condition, step.Wait?.Condition })
        {
            if (condition == null) continue;
            yield return condition.Left;
            if (condition.Right != null) yield return condition.Right;
        }
    }
}

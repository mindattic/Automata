using NUnit.Framework;
using Automata.Core.Automation.Demos;
using Automata.Core.Automation.Model;

namespace Automata.Tests;

/// <summary>
/// The generated examples are the only place a capability can be seen working, so this fixture
/// makes them the definition of done: <b>a new StepAction, WaitMode, ConditionOp or AggregateOp
/// fails the build until some seeded example demonstrates it.</b>
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
    /// <para>
    /// <see cref="BindingKind.EnvVar"/>: its whole point is a value that is NOT in the store, read
    /// from the machine at run time. A seeded example could only demonstrate it by depending on a
    /// variable the user has not set, which would ship an example that fails on every fresh
    /// install. The editor offers it and the resolver reads it; there is nowhere honest to show it
    /// HERE. The webmail acceptance profile does show it - it is where a mailbox password has to
    /// come from - which is exactly why that one is seeded on request rather than on launch, and
    /// why <see cref="AcceptanceProfileTests"/> asserts the binding rather than this file.
    /// </para>
    private static readonly HashSet<object> NotDemonstrable = [WaitMode.UntilSignal, BindingKind.EnvVar];

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

    [Test]
    public void EveryAggregateOpIsDemonstratedBySomeExample()
    {
        AssertCovered<AggregateOp>(
            AllSteps().Where(s => s.Aggregate != null).Select(s => (object)s.Aggregate!.Op));
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
    /// Every way a value can be reached has to be reachable in an example too.
    /// <para>
    /// A binding kind is worth checking for the same reason a step action is: it is a capability
    /// with a picker entry, and one nobody has ever run is one nobody knows works.
    /// </para>
    /// <para>
    /// What it does NOT prove, and the reason to say so here:
    /// <see cref="BindingKind.DatasetRow"/> is sited two ways — as a for-each's source and as a
    /// value — and every loop in every example satisfies this count with the first. That it also
    /// resolves to the row is held up by the roster example and by the engine's own tests, not by
    /// this one.
    /// </para>
    /// </summary>
    [Test]
    public void EveryBindingKindIsDemonstratedBySomeExample()
    {
        AssertCovered<BindingKind>(AllBindings().Select(b => (object)b.Kind));
    }

    private static IEnumerable<BindingRef> Values(Dictionary<string, BindingRef>? map) =>
        map?.Values ?? Enumerable.Empty<BindingRef>();

    /// <summary>Every BindingRef an example carries, wherever a step can hold one.</summary>
    private static IEnumerable<BindingRef> AllBindings()
    {
        foreach (var step in AllSteps())
        {
            foreach (var binding in Values(step.Bindings)) yield return binding;
            foreach (var binding in Values(step.RunTaskInputs)) yield return binding;
            foreach (var binding in Values(step.WriteDataset?.Columns)) yield return binding;
            if (step.ForEach?.Source is { } source) yield return source;
            foreach (var condition in new[] { step.Condition, step.Wait?.Condition })
            {
                if (condition == null) continue;
                yield return condition.Left;
                if (condition.Right != null) yield return condition.Right;
            }
        }
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
                    .Select(c => (object)c!.Op))
                .Concat(AllSteps().Where(s => s.Aggregate != null).Select(s => (object)s.Aggregate!.Op))
                .Concat(AllBindings().Select(b => (object)b.Kind)));

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
    /// <para>
    /// There is exactly one step whose own output is in scope for itself, and it is worth naming
    /// rather than waving through: a WATCHING wait reads its target and publishes the reading
    /// before its condition is evaluated, on every poll. So its condition binding points at itself
    /// by design, and a wait whose condition pointed at anything else would not be watching
    /// anything.
    /// </para>
    /// </summary>
    [Test]
    public void EveryStepOutputBindingNamesAnEarlierStepThatPublishesIt()
    {
        foreach (var demo in Demos())
        {
            var published = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in Walk(demo.Steps))
            {
                // Published BEFORE its own bindings are checked, and only for this one shape.
                if (Watches(step))
                    foreach (var output in step.Outputs ?? [])
                        published.Add($"{step.Id}/{output.Name}");

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

    /// <summary>A wait that re-reads an element every poll, rather than re-asking about values the
    /// run already holds. The target is what says which it is.</summary>
    private static bool Watches(Step step) =>
        step.Action == StepAction.Wait
        && step.Wait is { Mode: WaitMode.UntilCondition }
        && step.Target != null;

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

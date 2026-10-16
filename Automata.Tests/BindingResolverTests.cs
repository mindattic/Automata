using Automata.Core.Automation.Model;
using Automata.Core.Automation.Replay;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class BindingResolverTests
{
    // ---- Lookup: Literal ------------------------------------------------------------------

    [Test]
    public void Lookup_Literal_IsAlwaysFoundEvenWhenEmpty()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.Literal, Literal = null };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(""));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void Lookup_Literal_ReturnsTheLiteralText()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.Literal, Literal = "hello" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("hello"));
            Assert.That(error, Is.Null);
        });
    }

    // ---- Lookup: StepOutput -----------------------------------------------------------------

    [Test]
    public void Lookup_StepOutput_FoundWhenTheStepHasPublishedIt()
    {
        var state = new ReplayRunState();
        state.Outputs[ReplayRunState.OutputKey("step1", "total")] = "42";
        var binding = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "step1", OutputField = "total" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("42"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void Lookup_StepOutput_AbsentWhenTheStepHasNotRunYet()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "step1", OutputField = "total" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    // ---- Lookup: TaskInput ------------------------------------------------------------------

    [Test]
    public void Lookup_TaskInput_ErrorWhenNoParameterNameIsSet()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.TaskInput, ParameterName = "  " };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo("no input name set"));
        });
    }

    [Test]
    public void Lookup_TaskInput_FoundWhenSupplied()
    {
        var state = new ReplayRunState();
        state.SetRunInputs(new Dictionary<string, string> { ["name"] = "caller-value" });
        var binding = new BindingRef { Kind = BindingKind.TaskInput, ParameterName = "name" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("caller-value"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void Lookup_TaskInput_AbsentWhenNothingSuppliedIt()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.TaskInput, ParameterName = "name" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    // ---- Lookup: DatasetColumn --------------------------------------------------------------

    [Test]
    public void Lookup_DatasetColumn_ErrorWhenNoColumnNameIsSet()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo("no column name set"));
        });
    }

    [Test]
    public void Lookup_DatasetColumn_FoundWhenTheRowCarriesIt()
    {
        var state = new ReplayRunState();
        state.Variables["sku"] = "abc";
        var binding = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "sku" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("abc"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void Lookup_DatasetColumn_AbsentWhenTheRowDoesNotCarryIt()
    {
        var state = new ReplayRunState { InRowScope = true };
        var binding = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "missing" };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    // ---- Lookup: DatasetRow -----------------------------------------------------------------

    [Test]
    public void Lookup_DatasetRow_FoundWhenAnEnclosingLoopHasARow()
    {
        var state = new ReplayRunState();
        var child = state.ForkForRow("row", new Dictionary<string, string> { ["sku"] = "abc" }, 1, "items");
        var binding = new BindingRef { Kind = BindingKind.DatasetRow };

        var (found, value, error) = BindingResolver.Lookup(binding, child);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("""{"sku":"abc"}"""));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void Lookup_DatasetRow_AbsentWithNoEnclosingLoop()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.DatasetRow };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    // ---- Lookup: EnvVar ---------------------------------------------------------------------

    [Test]
    public void Lookup_EnvVar_ErrorWhenNoVariableNameIsSet()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.EnvVar, EnvVarName = null };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo("no environment variable name set"));
        });
    }

    [Test]
    public void Lookup_EnvVar_FoundWhenTheVariableIsSet()
    {
        var name = "AUTOMATA_TEST_" + Guid.NewGuid().ToString("n");
        Environment.SetEnvironmentVariable(name, "secret-value");
        try
        {
            var state = new ReplayRunState();
            var binding = new BindingRef { Kind = BindingKind.EnvVar, EnvVarName = name };

            var (found, value, error) = BindingResolver.Lookup(binding, state);

            Assert.Multiple(() =>
            {
                Assert.That(found, Is.True);
                Assert.That(value, Is.EqualTo("secret-value"));
                Assert.That(error, Is.Null);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Test]
    public void Lookup_EnvVar_AbsentWhenTheVariableIsNotSet()
    {
        var name = "AUTOMATA_TEST_UNSET_" + Guid.NewGuid().ToString("n");
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.EnvVar, EnvVarName = name };

        var (found, value, error) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(value, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    // ---- Prefix / Suffix ---------------------------------------------------------------------

    [Test]
    public void Lookup_WrapsAFoundValueInPrefixAndSuffix()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef
        {
            Kind = BindingKind.Literal,
            Literal = "item/123",
            Prefix = "https://shop.example/",
            Suffix = "?ref=automata",
        };

        var (found, value, _) = BindingResolver.Lookup(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo("https://shop.example/item/123?ref=automata"));
        });
    }

    [Test]
    public void Lookup_WithNoPrefixOrSuffixLeavesTheValueBare()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.Literal, Literal = "plain" };

        var (_, value, _) = BindingResolver.Lookup(binding, state);

        Assert.That(value, Is.EqualTo("plain"));
    }

    // ---- Missing messages ---------------------------------------------------------------------

    [Test]
    public void Resolve_StepOutput_MissingMessageSaysTheStepMustRunFirst()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.StepOutput, SourceStepId = "step1", OutputField = "total" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo(
                "'total' has not been produced yet — the step that publishes it must run first"));
        });
    }

    [Test]
    public void Resolve_TaskInput_MissingMessageSaysNothingSuppliedItAndNoDefault()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.TaskInput, ParameterName = "name" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo("nothing supplied the input 'name', and it has no default"));
        });
    }

    [Test]
    public void Resolve_DatasetColumn_OutOfRowScope_SaysTheBindingNeedsAnEnclosingForEach()
    {
        var state = new ReplayRunState { InRowScope = false };
        var binding = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "sku" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo(
                "no value for 'sku' here — this binding needs an enclosing for-each over a dataset"));
        });
    }

    [Test]
    public void Resolve_DatasetColumn_InRowScope_SaysTheRowHasNoSuchColumn()
    {
        var state = new ReplayRunState { InRowScope = true };
        var binding = new BindingRef { Kind = BindingKind.DatasetColumn, ColumnName = "sku" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo(
                "this row has no 'sku' — check the column name, or guard the step with 'exists'"));
        });
    }

    [Test]
    public void Resolve_DatasetRow_OutOfRowScope_SaysThereIsNoRowHere()
    {
        var state = new ReplayRunState { InRowScope = false };
        var binding = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "items" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo(
                "there is no row here — a whole-row binding needs an enclosing for-each over a dataset"));
        });
    }

    [Test]
    public void Resolve_DatasetRow_InRowScope_SaysNoLoopIsOverThatDataset()
    {
        var state = new ReplayRunState();
        var child = state.ForkForRow("row", new Dictionary<string, string> { ["sku"] = "abc" }, 1, "items");
        var binding = new BindingRef { Kind = BindingKind.DatasetRow, DatasetName = "otherSet" };

        var (value, error) = BindingResolver.Resolve(binding, child);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo(
                "no loop here is over 'otherSet' — check which dataset the whole-row binding names"));
        });
    }

    [Test]
    public void Resolve_EnvVar_MissingMessageNamesTheVariable()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.EnvVar, EnvVarName = "SOME_SECRET" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(error, Is.EqualTo("environment variable 'SOME_SECRET' is not set"));
        });
    }

    [Test]
    public void Resolve_FoundValuePassesThroughWithNoError()
    {
        var state = new ReplayRunState();
        var binding = new BindingRef { Kind = BindingKind.Literal, Literal = "value" };

        var (value, error) = BindingResolver.Resolve(binding, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo("value"));
            Assert.That(error, Is.Null);
        });
    }

    // ---- ResolveValues: Url field routing and overall wiring ----------------------------------

    [Test]
    public void ResolveValues_WithNoBindingsReturnsTheStepsOwnLiterals()
    {
        var step = new Step { Value = "literal-value", Url = "literal-url" };
        var state = new ReplayRunState();

        var (value, url, error) = BindingResolver.ResolveValues(step, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo("literal-value"));
            Assert.That(url, Is.EqualTo("literal-url"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void ResolveValues_RoutesTheUrlFieldToUrlAndEverythingElseToValue()
    {
        var step = new Step
        {
            Value = "old-value",
            Url = "old-url",
            Bindings = new Dictionary<string, BindingRef>
            {
                ["Value"] = new() { Kind = BindingKind.Literal, Literal = "new-value" },
                ["Url"] = new() { Kind = BindingKind.Literal, Literal = "new-url" },
            },
        };
        var state = new ReplayRunState();

        var (value, url, error) = BindingResolver.ResolveValues(step, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo("new-value"));
            Assert.That(url, Is.EqualTo("new-url"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void ResolveValues_UrlFieldMatchingIsCaseInsensitive()
    {
        var step = new Step
        {
            Url = "old-url",
            Bindings = new Dictionary<string, BindingRef>
            {
                ["url"] = new() { Kind = BindingKind.Literal, Literal = "new-url" },
            },
        };
        var state = new ReplayRunState();

        var (_, url, _) = BindingResolver.ResolveValues(step, state);

        Assert.That(url, Is.EqualTo("new-url"));
    }

    [Test]
    public void ResolveValues_SkipsANullBindingEntry()
    {
        var step = new Step
        {
            Value = "unchanged",
            Bindings = new Dictionary<string, BindingRef> { ["Value"] = null! },
        };
        var state = new ReplayRunState();

        var (value, _, error) = BindingResolver.ResolveValues(step, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.EqualTo("unchanged"));
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public void ResolveValues_ReturnsAFieldPrefixedErrorAndNoValuesWhenABindingFails()
    {
        var step = new Step
        {
            Value = "old-value",
            Bindings = new Dictionary<string, BindingRef>
            {
                ["Value"] = new() { Kind = BindingKind.TaskInput, ParameterName = "missing" },
            },
        };
        var state = new ReplayRunState();

        var (value, url, error) = BindingResolver.ResolveValues(step, state);

        Assert.Multiple(() =>
        {
            Assert.That(value, Is.Null);
            Assert.That(url, Is.Null);
            Assert.That(error, Is.EqualTo(
                "binding for Value: nothing supplied the input 'missing', and it has no default"));
        });
    }
}

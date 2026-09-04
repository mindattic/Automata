using Automata.Core.Automation.Model;
using Automata.Core.Automation.Settings;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class EngineSettingsResolverTests
{
    /// <summary>
    /// The load-bearing test of the whole scoped-settings feature: with nothing overridden
    /// anywhere, the resolver must reproduce the engine's behavior from before scopes existed.
    /// Every field is pinned, so widening the record without deciding its floor breaks here.
    /// </summary>
    [Test]
    public void Floor_ReproducesThePreScopesBehaviour()
    {
        var s = EngineSettingsResolver.Resolve();

        Assert.Multiple(() =>
        {
            Assert.That(s.DefaultStepTimeoutMs, Is.EqualTo(10_000));
            Assert.That(s.SelfHeal, Is.True);
            Assert.That(s.AllowLlmRepair, Is.False);
            Assert.That(s.Retry.MaxAttempts, Is.EqualTo(1));
            // Asymmetric on purpose: a failed step aborts its task, a failed task does not abort
            // its collection. That is exactly what ReplayEngine and RunCollectionAsync did.
            Assert.That(s.ContinueOnStepError, Is.False);
            Assert.That(s.ContinueOnTaskError, Is.True);
            Assert.That(s.Isolation, Is.EqualTo(FailureIsolation.IsolateLane));
            Assert.That(s.MaxConcurrency, Is.EqualTo(1));
            Assert.That(s.BrowserProfile, Is.EqualTo("default"));
            Assert.That(s.ScreenshotOnFailure, Is.False);
            Assert.That(s.LlmProvider, Is.EqualTo("claude"));
        });
    }

    [Test]
    public void EmptyOverridesAtEveryScope_ChangeNothing()
    {
        var s = EngineSettingsResolver.Resolve(
            new AutomataSettings { EngineDefaults = new EngineSettingsOverride() },
            new EngineSettingsOverride(), new EngineSettingsOverride(), new EngineSettingsOverride());

        Assert.That(s, Is.EqualTo(EngineSettingsResolver.Floor()));
    }

    [Test]
    public void DeeperScopeWins()
    {
        var s = EngineSettingsResolver.Resolve(
            new AutomataSettings { EngineDefaults = new EngineSettingsOverride { DefaultStepTimeoutMs = 1000 } },
            new EngineSettingsOverride { DefaultStepTimeoutMs = 2000 },
            new EngineSettingsOverride { DefaultStepTimeoutMs = 3000 },
            new EngineSettingsOverride { DefaultStepTimeoutMs = 4000 });

        Assert.That(s.DefaultStepTimeoutMs, Is.EqualTo(4000));
    }

    [Test]
    public void AScopeThatSaysNothing_DoesNotMaskAnOuterScope()
    {
        var s = EngineSettingsResolver.Resolve(
            collection: new EngineSettingsOverride { SelfHeal = false },
            task: new EngineSettingsOverride { AllowLlmRepair = true },
            step: new EngineSettingsOverride());

        Assert.That(s.SelfHeal, Is.False, "the collection's value should survive an empty task and step");
        Assert.That(s.AllowLlmRepair, Is.True);
    }

    [Test]
    public void MaxConcurrency_GlobalIsACeilingADeeperScopeCanOnlyLower()
    {
        var global = new AutomataSettings { EngineDefaults = new EngineSettingsOverride { MaxConcurrency = 4 } };

        Assert.That(EngineSettingsResolver.Resolve(global).MaxConcurrency, Is.EqualTo(4));
        Assert.That(EngineSettingsResolver.Resolve(global, new EngineSettingsOverride { MaxConcurrency = 2 })
            .MaxConcurrency, Is.EqualTo(2), "a collection may tighten");
        Assert.That(EngineSettingsResolver.Resolve(global, task: new EngineSettingsOverride { MaxConcurrency = 99 })
            .MaxConcurrency, Is.EqualTo(4), "a task must not out-declare the global ceiling");
    }

    [Test]
    public void MaxConcurrency_TightestScopeWinsRegardlessOfDepth()
    {
        var s = EngineSettingsResolver.Resolve(
            new AutomataSettings { EngineDefaults = new EngineSettingsOverride { MaxConcurrency = 8 } },
            new EngineSettingsOverride { MaxConcurrency = 2 },
            new EngineSettingsOverride { MaxConcurrency = 6 });

        Assert.That(s.MaxConcurrency, Is.EqualTo(2));
    }

    [Test]
    public void LlmProvider_FallsBackToTheGlobalProviderSetting()
    {
        var s = EngineSettingsResolver.Resolve(new AutomataSettings { Provider = "gemini" });

        Assert.That(s.LlmProvider, Is.EqualTo("gemini"),
            "the existing Settings dialog's provider must keep driving runs with no scope set");
    }

    [Test]
    public void LlmProvider_ScopeOverridesTheGlobalSetting()
    {
        var s = EngineSettingsResolver.Resolve(
            new AutomataSettings { Provider = "gemini" },
            task: new EngineSettingsOverride { LlmProvider = "kimi" });

        Assert.That(s.LlmProvider, Is.EqualTo("kimi"));
    }

    [Test]
    public void BlankStringOverrides_AreTreatedAsInherit()
    {
        var s = EngineSettingsResolver.Resolve(
            new AutomataSettings { Provider = "openai" },
            new EngineSettingsOverride { BrowserProfile = "work" },
            new EngineSettingsOverride { BrowserProfile = "   ", LlmProvider = "" });

        Assert.That(s.BrowserProfile, Is.EqualTo("work"), "whitespace must not blank out an inherited profile");
        Assert.That(s.LlmProvider, Is.EqualTo("openai"));
    }

    [Test]
    public void NonPositiveTimeout_IsIgnoredRatherThanCollapsingTheBudget()
    {
        var s = EngineSettingsResolver.Resolve(
            collection: new EngineSettingsOverride { DefaultStepTimeoutMs = 5000 },
            task: new EngineSettingsOverride { DefaultStepTimeoutMs = 0 });

        Assert.That(s.DefaultStepTimeoutMs, Is.EqualTo(5000));
    }

    [Test]
    public void IsEmpty_IsTrueOnlyWhenNothingIsOverridden()
    {
        Assert.That(new EngineSettingsOverride().IsEmpty, Is.True);
        Assert.That(new EngineSettingsOverride { SelfHeal = false }.IsEmpty, Is.False);
        Assert.That(new EngineSettingsOverride { BrowserProfile = "" }.IsEmpty, Is.True,
            "a blank profile is not an override");
        Assert.That(new EngineSettingsOverride { Retry = new RetryPolicy() }.IsEmpty, Is.False);
    }
}

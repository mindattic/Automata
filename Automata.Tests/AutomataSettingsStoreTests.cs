using Automata.Core.Automation.Model;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class AutomataSettingsStoreTests
{
    private TestDb db = null!;

    [SetUp]
    public void SetUp() => db = new TestDb();

    [TearDown]
    public void TearDown() => db.Dispose();

    [Test]
    public void Load_BeforeAnythingIsSaved_ReturnsDefaults()
    {
        var store = db.Settings();
        var settings = store.Load();

        Assert.Multiple(() =>
        {
            Assert.That(settings.Provider, Is.EqualTo("claude"));
            Assert.That(settings.BorderRadius, Is.EqualTo(5));
            Assert.That(settings.SidebarWidth, Is.EqualTo(420));
            Assert.That(settings.EngineDefaults, Is.Null);
            Assert.That(store.HasSaved, Is.False);
        });
    }

    [Test]
    public void SaveThenLoad_RoundTrips()
    {
        var store = db.Settings();

        store.Save(new AutomataSettings
        {
            Provider = "gemini",
            BorderRadius = 8,
            SidebarWidth = 512,
            Theme = AutomataSettings.Themes.Light,
            PanelDetached = true,
            PanelWindowLeft = -1200,
        });
        var back = db.Settings().Load();

        Assert.Multiple(() =>
        {
            Assert.That(back.Provider, Is.EqualTo("gemini"));
            Assert.That(back.BorderRadius, Is.EqualTo(8));
            Assert.That(back.SidebarWidth, Is.EqualTo(512));
            Assert.That(back.Theme, Is.EqualTo("light"));
            Assert.That(back.PanelDetached, Is.True);
            Assert.That(back.PanelWindowLeft, Is.EqualTo(-1200));
            Assert.That(back.PanelWindowTop, Is.Null, "never placed stays never placed");
            Assert.That(store.HasSaved, Is.True);
        });
    }

    [Test]
    public void SavingAgainUpdatesTheOneRow()
    {
        var store = db.Settings();
        store.Save(new AutomataSettings { Provider = "openai" });
        var settings = store.Load();
        settings.Provider = "kimi";
        store.Save(settings);

        Assert.That(store.Load().Provider, Is.EqualTo("kimi"));
    }

    /// <summary>The engine defaults are a complex type on the settings row — including the nested
    /// retry policy — and "no override" must come back as no override, not an empty one.</summary>
    [Test]
    public void EngineDefaultsRoundTripAsAComplexType()
    {
        var store = db.Settings();
        store.Save(new AutomataSettings
        {
            EngineDefaults = new EngineSettingsOverride
            {
                SelfHeal = false,
                DefaultStepTimeoutMs = 9000,
                Retry = new RetryPolicy { MaxAttempts = 3, DelayMs = 500, BackoffMultiplier = 2 },
            },
        });

        var back = store.Load().EngineDefaults!;
        Assert.Multiple(() =>
        {
            Assert.That(back.SelfHeal, Is.False);
            Assert.That(back.DefaultStepTimeoutMs, Is.EqualTo(9000));
            Assert.That(back.AllowLlmRepair, Is.Null, "unset stays inherit");
            Assert.That(back.Retry, Is.EqualTo(new RetryPolicy { MaxAttempts = 3, DelayMs = 500, BackoffMultiplier = 2 }));
        });

        var cleared = store.Load();
        cleared.EngineDefaults = new EngineSettingsOverride();
        store.Save(cleared);
        Assert.That(store.Load().EngineDefaults, Is.Null, "an override of nothing is stored as none");
    }

    [Test]
    public void Provider_DefaultsToClaude()
    {
        Assert.That(db.Settings().Load().Provider, Is.EqualTo("claude"));
    }
}

using System.IO;
using Automata.Core.Automation.Storage;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class AutomataSettingsStoreTests
{
    private string path = null!;

    [SetUp]
    public void SetUp() => path = Path.Combine(
        Path.GetTempPath(), "automata-tests", Guid.NewGuid().ToString("n"), "settings.json");

    [TearDown]
    public void TearDown()
    {
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Test]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var settings = new AutomataSettingsStore(path).Load();

        Assert.That(settings.AnthropicApiKey, Is.Null);
        Assert.That(settings.BorderRadius, Is.EqualTo(5));
        Assert.That(settings.SidebarWidth, Is.EqualTo(420));
    }

    [Test]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new AutomataSettingsStore(path);

        store.Save(new AutomataSettings
        {
            Provider = "gemini",
            AnthropicApiKey = "sk-ant-test123",
            OpenAiApiKey = "sk-oai",
            GeminiApiKey = "AIza-g",
            KimiApiKey = "sk-kimi",
            BorderRadius = 8,
            SidebarWidth = 512,
        });
        var back = store.Load();

        Assert.That(back.Provider, Is.EqualTo("gemini"));
        Assert.That(back.AnthropicApiKey, Is.EqualTo("sk-ant-test123"));
        Assert.That(back.OpenAiApiKey, Is.EqualTo("sk-oai"));
        Assert.That(back.GeminiApiKey, Is.EqualTo("AIza-g"));
        Assert.That(back.KimiApiKey, Is.EqualTo("sk-kimi"));
        Assert.That(back.BorderRadius, Is.EqualTo(8));
        Assert.That(back.SidebarWidth, Is.EqualTo(512));
    }

    [Test]
    public void Provider_DefaultsToClaude()
    {
        Assert.That(new AutomataSettingsStore(path).Load().Provider, Is.EqualTo("claude"));
    }

    [Test]
    public void Load_WithCorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var settings = new AutomataSettingsStore(path).Load();

        Assert.That(settings.AnthropicApiKey, Is.Null);
        Assert.That(settings.BorderRadius, Is.EqualTo(5));
    }

    /// <summary>
    /// A settings.json written before SidebarWidth existed must still load - the property simply
    /// falls back to its default rather than deserializing as 0, which would collapse the sidebar.
    /// </summary>
    [Test]
    public void Load_FromAPreSidebarWidthFile_FallsBackToTheDefaultWidth()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "provider": "claude", "borderRadius": 3 }""");

        var settings = new AutomataSettingsStore(path).Load();

        Assert.That(settings.BorderRadius, Is.EqualTo(3));
        Assert.That(settings.SidebarWidth, Is.EqualTo(420));
    }

    [Test]
    public void ClearingTheKey_PersistsAsNull()
    {
        var store = new AutomataSettingsStore(path);
        store.Save(new AutomataSettings { AnthropicApiKey = "sk-ant-x" });

        var settings = store.Load();
        settings.AnthropicApiKey = null;
        store.Save(settings);

        Assert.That(store.Load().AnthropicApiKey, Is.Null);
        Assert.That(File.ReadAllText(path), Does.Not.Contain("sk-ant-x"));
    }
}

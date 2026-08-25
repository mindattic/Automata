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
    }

    [Test]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new AutomataSettingsStore(path);

        store.Save(new AutomataSettings { AnthropicApiKey = "sk-ant-test123", BorderRadius = 8 });
        var back = store.Load();

        Assert.That(back.AnthropicApiKey, Is.EqualTo("sk-ant-test123"));
        Assert.That(back.BorderRadius, Is.EqualTo(8));
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

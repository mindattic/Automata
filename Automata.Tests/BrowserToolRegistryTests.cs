using Automata.Core.Operator;
using Automata.Core.Operator.Tools;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class BrowserToolRegistryTests
{
    private static readonly IBrowserTool[] AllTools =
    [
        new ClickButtonTool(),
        new CheckCheckboxTool(),
        new SelectFormOptionTool(),
        new SetFieldTool(),
        new TypeIntoFieldTool(),
        new UploadFileTool(),
        new GetPageStatusTool(),
        new LogNoteTool(),
    ];

    [Test]
    public void BuildToolDefinitions_ReturnsOneDefinitionPerTool_WithValidJsonSchema()
    {
        var registry = new BrowserToolRegistry(AllTools);

        var defs = registry.BuildToolDefinitions();

        Assert.That(defs, Has.Count.EqualTo(AllTools.Length));
        foreach (var def in defs)
        {
            Assert.That(def.Name, Is.Not.Empty);
            Assert.That(def.Description, Is.Not.Empty);
            Assert.That(def.InputSchema, Is.Not.Null);
        }
    }

    [Test]
    public void Get_ReturnsRegisteredTool_ByExactName()
    {
        var registry = new BrowserToolRegistry(AllTools);

        var tool = registry.Get("click_button");

        Assert.That(tool, Is.Not.Null);
        Assert.That(tool!.Name, Is.EqualTo("click_button"));
    }

    [Test]
    public void Get_ReturnsNull_ForUnknownToolName()
    {
        var registry = new BrowserToolRegistry(AllTools);

        Assert.That(registry.Get("does_not_exist"), Is.Null);
    }

    [Test]
    public void All_ExposesEveryRegisteredTool()
    {
        var registry = new BrowserToolRegistry(AllTools);

        Assert.That(registry.All, Has.Count.EqualTo(AllTools.Length));
    }

    [Test]
    public void EveryTool_HasAUniqueName()
    {
        var names = AllTools.Select(t => t.Name).ToList();

        Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count));
    }
}

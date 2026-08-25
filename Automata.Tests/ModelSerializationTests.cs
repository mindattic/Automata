using System.Text.Json;
using Automata.Core.Automation.Model;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ModelSerializationTests
{
    private static TaskDefinition SampleTask() => new()
    {
        Id = "41ab8c1e0f6d4d0f9a4b7f2a3c5d6e7f",
        CollectionId = "9f2c0a1b2c3d4e5f6a7b8c9d0e1f2a3b",
        Name = "Search Google for cats",
        Description = "Demo end-to-end proof point",
        StartUrl = "https://www.google.com",
        CreatedUtc = new DateTimeOffset(2026, 8, 25, 14, 3, 22, TimeSpan.Zero),
        ModifiedUtc = new DateTimeOffset(2026, 8, 25, 14, 10, 5, TimeSpan.Zero),
        Steps =
        [
            new Step { Id = "s1", Action = StepAction.Navigate, Label = "Go to Google", Url = "https://www.google.com" },
            new Step
            {
                Id = "s2",
                Action = StepAction.TypeText,
                Label = "Type search query",
                Value = "cats",
                Target = new ElementFingerprint
                {
                    Tag = "textarea",
                    NameAttr = "q",
                    AriaRole = "combobox",
                    AriaLabel = "Search",
                    CssSelector = "textarea[name=\"q\"]",
                    XPath = "/html/body/div[1]/form//textarea",
                    ClassList = ["gLFyf"],
                    NearbyLabelText = "Search",
                },
            },
            new Step
            {
                Id = "s3",
                Action = StepAction.Click,
                Label = "Click 'Google Search'",
                IsCommitPoint = true,
                Target = new ElementFingerprint
                {
                    Tag = "input",
                    TypeAttr = "submit",
                    NameAttr = "btnK",
                    VisibleText = "Google Search",
                    CssSelector = "input[name=\"btnK\"]",
                },
            },
            new Step
            {
                Id = "s4",
                Action = StepAction.Group,
                Label = "Verify results",
                Children =
                [
                    new Step
                    {
                        Id = "s4a",
                        Action = StepAction.WaitForElement,
                        Label = "Results container",
                        TimeoutMs = 15000,
                        Target = new ElementFingerprint { Tag = "div", Id = "search", CssSelector = "#search" },
                    },
                    new Step
                    {
                        Id = "s4b",
                        Action = StepAction.ExtractText,
                        Label = "First result title",
                        PauseForUser = true,
                        Target = new ElementFingerprint { Tag = "h3", CssSelector = "#search h3", ClassList = ["LC20lb"] },
                    },
                ],
            },
        ],
    };

    [Test]
    public void TaskDefinition_RoundTrips_IncludingNestedSteps()
    {
        var task = SampleTask();

        var json = JsonSerializer.Serialize(task, AutomataJson.Options);
        var back = JsonSerializer.Deserialize<TaskDefinition>(json, AutomataJson.Options)!;

        Assert.That(back.Id, Is.EqualTo(task.Id));
        Assert.That(back.CollectionId, Is.EqualTo(task.CollectionId));
        Assert.That(back.StartUrl, Is.EqualTo(task.StartUrl));
        Assert.That(back.CreatedUtc, Is.EqualTo(task.CreatedUtc));
        Assert.That(back.Steps, Has.Count.EqualTo(4));
        Assert.That(back.Steps[1].Target!.NameAttr, Is.EqualTo("q"));
        Assert.That(back.Steps[2].IsCommitPoint, Is.True);
        Assert.That(back.Steps[3].Children, Has.Count.EqualTo(2));
        Assert.That(back.Steps[3].Children[0].TimeoutMs, Is.EqualTo(15000));
        Assert.That(back.Steps[3].Children[1].PauseForUser, Is.True);
        Assert.That(back.Steps[3].Children[1].Target!.ClassList, Is.EqualTo(new[] { "LC20lb" }));
    }

    [Test]
    public void Collection_RoundTrips()
    {
        var collection = new Collection
        {
            Name = "Email checks",
            Description = "Inbox automations",
            CreatedUtc = DateTimeOffset.UnixEpoch,
            ModifiedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            TaskOrder = ["t1", "t2"],
        };

        var json = JsonSerializer.Serialize(collection, AutomataJson.Options);
        var back = JsonSerializer.Deserialize<Collection>(json, AutomataJson.Options)!;

        Assert.That(back.Id, Is.EqualTo(collection.Id));
        Assert.That(back.Name, Is.EqualTo("Email checks"));
        Assert.That(back.TaskOrder, Is.EqualTo(new[] { "t1", "t2" }));
    }

    [Test]
    public void StepAction_SerializesAsCamelCaseString()
    {
        var json = JsonSerializer.Serialize(
            new Step { Action = StepAction.SelectOption, Label = "x" }, AutomataJson.Options);

        Assert.That(json, Does.Contain("\"action\": \"selectOption\""));
    }

    [Test]
    public void NullProperties_AreOmittedFromJson()
    {
        var json = JsonSerializer.Serialize(
            new Step { Action = StepAction.Navigate, Label = "Go", Url = "https://x.example" },
            AutomataJson.Options);

        Assert.That(json, Does.Not.Contain("\"target\""));
        Assert.That(json, Does.Not.Contain("\"value\""));
        Assert.That(json, Does.Not.Contain("\"timeoutMs\""));
    }

    [Test]
    public void UnknownJsonProperties_AreTolerated()
    {
        const string json = """
            {
              "schemaVersion": 7,
              "id": "abc",
              "name": "Future task",
              "steps": [],
              "someFutureField": { "nested": true }
            }
            """;

        var task = JsonSerializer.Deserialize<TaskDefinition>(json, AutomataJson.Options)!;

        Assert.That(task.SchemaVersion, Is.EqualTo(7));
        Assert.That(task.Name, Is.EqualTo("Future task"));
    }

    [Test]
    public void NewInstances_GetDistinct32CharGuidIds()
    {
        var a = new TaskDefinition();
        var b = new TaskDefinition();

        Assert.That(a.Id, Has.Length.EqualTo(32));
        Assert.That(a.Id, Is.Not.EqualTo(b.Id));
    }
}

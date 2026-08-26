using System.Text.Json.Nodes;
using Automata.Core.Operator;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class ProviderRosterTests
{
    private sealed class StubLlm(string name) : IToolCallingLlm
    {
        public string Name => name;
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);
        public Task<ToolTurnResult> CreateTurnAsync(string s, IReadOnlyList<ToolLoopMessage> h,
            IReadOnlyList<ToolDefinition> t, int m, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static ProviderRoster Roster(Func<string?> selected) => new(
    [
        ("claude", new StubLlm("Claude")),
        ("openai", new StubLlm("OpenAI")),
        ("gemini", new StubLlm("Gemini")),
        ("kimi", new StubLlm("Kimi")),
    ], selected);

    [Test]
    public void SelectedProvider_EnumeratesFirst_RestKeepRegistrationOrder()
    {
        var roster = Roster(() => "gemini");

        Assert.That(roster.Select(l => l.Name),
            Is.EqualTo(new[] { "Gemini", "Claude", "OpenAI", "Kimi" }));
        Assert.That(roster.Count, Is.EqualTo(4));
        Assert.That(roster[0].Name, Is.EqualTo("Gemini"));
    }

    [Test]
    public void ChangingSelection_ReordersTheNextEnumeration_WithoutRebuildingTheRoster()
    {
        var selected = "claude";
        var roster = Roster(() => selected);

        Assert.That(roster.First().Name, Is.EqualTo("Claude"));
        selected = "kimi";
        Assert.That(roster.First().Name, Is.EqualTo("Kimi")); // live re-order, same instance
    }

    [Test]
    public void UnknownOrNullSelection_FallsBackToRegistrationOrder()
    {
        Assert.That(Roster(() => null).First().Name, Is.EqualTo("Claude"));
        Assert.That(Roster(() => "nonsense").First().Name, Is.EqualTo("Claude"));
        Assert.That(Roster(() => " GEMINI ").First().Name, Is.EqualTo("Gemini")); // trimmed, case-insensitive
    }
}

[TestFixture]
public class GeminiToolCallingLlmTests
{
    [Test]
    public async Task IsConfigured_ReflectsKeyResolver()
    {
        using var http = new HttpClient();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiToolCallingLlm>.Instance;

        Assert.That(await new GeminiToolCallingLlm(http, log, () => "AIza-test").IsConfiguredAsync(), Is.True);
        Assert.That(await new GeminiToolCallingLlm(http, log, () => null).IsConfiguredAsync(), Is.False);
    }

    [Test]
    public void History_TranslatesToGeminiContents_WithNameCorrelatedFunctionResponses()
    {
        var history = new List<ToolLoopMessage>
        {
            new ToolLoopMessage.UserText("search for cats"),
            new ToolLoopMessage.AssistantTurn(
            [
                new AssistantPart.Text("Clicking now."),
                new AssistantPart.ToolCall("gemini-call-0", "click_button", """{ "text": "Search" }"""),
            ]),
            new ToolLoopMessage.ToolResults(
            [
                new ToolResultPart("gemini-call-0", """{ "clicked": true }""", false),
            ]),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        Assert.That(contents, Has.Count.EqualTo(3));
        Assert.That(contents[0]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        Assert.That(contents[1]!["role"]!.GetValue<string>(), Is.EqualTo("model"));
        var call = contents[1]!["parts"]![1]!["functionCall"]!;
        Assert.That(call["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(call["args"]!["text"]!.GetValue<string>(), Is.EqualTo("Search"));
        // The result correlates back by NAME (Gemini has no tool-call ids).
        var response = contents[2]!["parts"]![0]!["functionResponse"]!;
        Assert.That(contents[2]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        Assert.That(response["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(response["response"]!["clicked"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void GeminiParts_TranslateToNeutralShape_WithSynthesizedCallIds()
    {
        var parts = (JsonArray)JsonNode.Parse("""
            [
              { "text": "I'll click the button." },
              { "functionCall": { "name": "click_button", "args": { "text": "Search" } } },
              { "functionCall": { "name": "log_note", "args": { "message": "done" } } }
            ]
            """)!;

        var result = GeminiToolCallingLlm.FromGeminiParts(parts);

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0], Is.InstanceOf<AssistantPart.Text>());
        var calls = result.OfType<AssistantPart.ToolCall>().ToList();
        Assert.That(calls.Select(c => c.Id), Is.Unique);
        Assert.That(calls[0].Name, Is.EqualTo("click_button"));
        Assert.That(calls[0].ArgumentsJson, Does.Contain("Search"));
    }

    [Test]
    public void NonObjectToolResult_IsWrappedForGemini()
    {
        var history = new List<ToolLoopMessage>
        {
            new ToolLoopMessage.AssistantTurn([new AssistantPart.ToolCall("c1", "get_page_status", "{}")]),
            new ToolLoopMessage.ToolResults([new ToolResultPart("c1", "plain text, not json", false)]),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var response = contents[1]!["parts"]![0]!["functionResponse"]!["response"]!;
        Assert.That(response["result"]!.GetValue<string>(), Is.EqualTo("plain text, not json"));
    }
}

[TestFixture]
public class OpenAiCompatibleProviderTests
{
    [Test]
    public void KimiInstance_CarriesItsOwnName()
    {
        using var http = new HttpClient();
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAiToolCallingLlm>.Instance;

        var kimi = new OpenAiToolCallingLlm(http, log, () => "sk-kimi", model: "kimi-latest",
            name: "Kimi", endpoint: "https://api.moonshot.ai/v1/chat/completions");

        Assert.That(kimi.Name, Is.EqualTo("Kimi"));
        Assert.That(new OpenAiToolCallingLlm(http, log, () => "sk").Name, Is.EqualTo("OpenAI"));
    }
}

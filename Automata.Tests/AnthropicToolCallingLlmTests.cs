using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers <see cref="AnthropicToolCallingLlm"/>'s translation layer: history
/// (<see cref="ToolLoopMessage"/>) mapped into Anthropic's message shape on the way out, and
/// response content blocks parsed into <see cref="AssistantPart"/> on the way back. Both
/// directions are exercised end-to-end through a stub <see cref="HttpMessageHandler"/> rather than
/// reaching into the adapter's private mapping methods.
/// </summary>
[TestFixture]
public class AnthropicToolCallingLlmTests
{
    private static AnthropicToolCallingLlm MakeLlm(CapturingHandler handler) =>
        new(new AnthropicToolClient(new HttpClient(handler), NullLogger<AnthropicToolClient>.Instance),
            () => "sk-ant-oat-test");

    [Test]
    public async Task CreateTurnAsync_MapsHistoryIntoAnthropicMessageShape()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"content":[],"stop_reason":"end_turn"}""");
        var llm = MakeLlm(handler);

        var history = new List<ToolLoopMessage>
        {
            new ToolLoopMessage.UserText("find the button"),
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.Text("I'll click it"),
                new AssistantPart.ToolCall("call-1", "click_button", """{"selector":"#go"}"""),
            }),
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("call-1", "clicked", IsError: false),
                new ToolResultPart("call-2", "not found", IsError: true),
            }),
        };

        await llm.CreateTurnAsync("system prompt", history, Array.Empty<ToolDefinition>(), 100, default);

        var body = JsonNode.Parse(handler.LastRequestBody)!;
        var messages = body["messages"]!.AsArray();
        Assert.That(messages, Has.Count.EqualTo(3));

        Assert.That(messages[0]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        Assert.That(messages[0]!["content"]![0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
        Assert.That(messages[0]!["content"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("find the button"));

        Assert.That(messages[1]!["role"]!.GetValue<string>(), Is.EqualTo("assistant"));
        var assistantContent = messages[1]!["content"]!.AsArray();
        Assert.That(assistantContent[0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
        Assert.That(assistantContent[0]!["text"]!.GetValue<string>(), Is.EqualTo("I'll click it"));
        Assert.That(assistantContent[1]!["type"]!.GetValue<string>(), Is.EqualTo("tool_use"));
        Assert.That(assistantContent[1]!["id"]!.GetValue<string>(), Is.EqualTo("call-1"));
        Assert.That(assistantContent[1]!["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(assistantContent[1]!["input"]!["selector"]!.GetValue<string>(), Is.EqualTo("#go"));

        Assert.That(messages[2]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        var resultsContent = messages[2]!["content"]!.AsArray();
        Assert.That(resultsContent[0]!["type"]!.GetValue<string>(), Is.EqualTo("tool_result"));
        Assert.That(resultsContent[0]!["tool_use_id"]!.GetValue<string>(), Is.EqualTo("call-1"));
        Assert.That(resultsContent[0]!["content"]!.GetValue<string>(), Is.EqualTo("clicked"));
        Assert.That(resultsContent[0]!["is_error"]!.GetValue<bool>(), Is.False);
        Assert.That(resultsContent[1]!["tool_use_id"]!.GetValue<string>(), Is.EqualTo("call-2"));
        Assert.That(resultsContent[1]!["content"]!.GetValue<string>(), Is.EqualTo("not found"));
        Assert.That(resultsContent[1]!["is_error"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task CreateTurnAsync_ParsesTextAndToolUseContentBlocksIntoAssistantParts()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """
            {"content":[
                {"type":"text","text":"looking now"},
                {"type":"tool_use","id":"call-9","name":"take_screenshot","input":{"full_page":true}}
            ],"stop_reason":"tool_use"}
            """);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("system", new[] { new ToolLoopMessage.UserText("go") },
            Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(result.Parts, Has.Count.EqualTo(2));
        var text = (AssistantPart.Text)result.Parts[0];
        Assert.That(text.Value, Is.EqualTo("looking now"));
        var call = (AssistantPart.ToolCall)result.Parts[1];
        Assert.That(call.Id, Is.EqualTo("call-9"));
        Assert.That(call.Name, Is.EqualTo("take_screenshot"));
        Assert.That(JsonNode.Parse(call.ArgumentsJson)!["full_page"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task CreateTurnAsync_IgnoresEmptyTextBlocks()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """
            {"content":[{"type":"text","text":""}],"stop_reason":"end_turn"}
            """);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("system", new[] { new ToolLoopMessage.UserText("go") },
            Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(result.Parts, Is.Empty);
    }

    /// <summary>Returns a fixed response body and captures the single request body sent.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;
        public string LastRequestBody { get; private set; } = "";

        public CapturingHandler(HttpStatusCode status, string body)
        {
            this.status = status;
            this.body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}

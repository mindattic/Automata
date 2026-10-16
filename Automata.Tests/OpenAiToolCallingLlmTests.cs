using System.Net;
using System.Text.Json.Nodes;
using Automata.Core.Operator;
using Automata.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers <see cref="OpenAiToolCallingLlm"/>'s wire translation in both directions — request body
/// shape (system prompt placement, tools-array omission, max_tokens, history mapping) and response
/// parsing (text/tool_calls) — plus the provider-specific retry behaviour: OpenAI only retries 429,
/// unlike Gemini which also retries 5xx (<see cref="GeminiToolCallingLlmTests"/>). A mapping bug
/// here silently breaks AI step repair for this provider, so each shape is checked directly against
/// the captured request JSON rather than just asserting the call succeeded.
/// </summary>
[TestFixture]
public class OpenAiToolCallingLlmTests
{
    private const string TextOnlyResponse = """{"choices":[{"message":{"content":"hello"}}]}""";

    private static OpenAiToolCallingLlm MakeLlm(ScriptedHttpMessageHandler handler, string key = "sk-test") =>
        new(new HttpClient(handler), NullLogger<OpenAiToolCallingLlm>.Instance, () => key);

    private static (string SystemPrompt, IReadOnlyList<ToolLoopMessage> History, IReadOnlyList<ToolDefinition> Tools) SimpleTurn(string systemPrompt = "be helpful") =>
        (systemPrompt, new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>());

    // ---- Request body shape ----------------------------------------------------------------

    [Test]
    public async Task RequestBody_SystemPromptIsFirstMessage()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        var (systemPrompt, history, tools) = SimpleTurn("you are a browser operator");
        await llm.CreateTurnAsync(systemPrompt, history, tools, 1000, default);

        var messages = handler.RequestBodies[0]!["messages"]!.AsArray();
        Assert.That(messages[0]!["role"]!.GetValue<string>(), Is.EqualTo("system"));
        Assert.That(messages[0]!["content"]!.GetValue<string>(), Is.EqualTo("you are a browser operator"));
    }

    [Test]
    public async Task RequestBody_ToolsOmittedWhenEmpty()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        var (systemPrompt, history, tools) = SimpleTurn();
        await llm.CreateTurnAsync(systemPrompt, history, tools, 1000, default);

        Assert.That(handler.RequestBodies[0]!.AsObject().ContainsKey("tools"), Is.False);
    }

    [Test]
    public async Task RequestBody_ToolsIncludedWhenPresent_MapsToFunctionShape()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var schema = new JsonObject { ["type"] = "object" };
        var tools = new[] { new ToolDefinition("click_button", "Clicks a button", schema) };

        await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, tools, 1000, default);

        var toolsArr = handler.RequestBodies[0]!["tools"]!.AsArray();
        Assert.That(toolsArr, Has.Count.EqualTo(1));
        Assert.That(toolsArr[0]!["type"]!.GetValue<string>(), Is.EqualTo("function"));
        var fn = toolsArr[0]!["function"]!;
        Assert.That(fn["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(fn["description"]!.GetValue<string>(), Is.EqualTo("Clicks a button"));
        Assert.That(fn["parameters"]!["type"]!.GetValue<string>(), Is.EqualTo("object"));
    }

    [Test]
    public async Task RequestBody_MaxTokensMapsToMaxTokensField()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        var (systemPrompt, history, tools) = SimpleTurn();
        await llm.CreateTurnAsync(systemPrompt, history, tools, 4096, default);

        Assert.That(handler.RequestBodies[0]!["max_tokens"]!.GetValue<int>(), Is.EqualTo(4096));
    }

    [Test]
    public async Task RequestBody_UserTextMapsToUserMessage()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("go to the page") }, Array.Empty<ToolDefinition>(), 100, default);

        var messages = handler.RequestBodies[0]!["messages"]!.AsArray();
        Assert.That(messages[1]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        Assert.That(messages[1]!["content"]!.GetValue<string>(), Is.EqualTo("go to the page"));
    }

    [Test]
    public async Task RequestBody_AssistantTextOnly_HasContentAndNoToolCalls()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart> { new AssistantPart.Text("thinking") }),
        };

        await llm.CreateTurnAsync("sys", history, Array.Empty<ToolDefinition>(), 100, default);

        var assistantMsg = handler.RequestBodies[0]!["messages"]!.AsArray()[1]!;
        Assert.That(assistantMsg["role"]!.GetValue<string>(), Is.EqualTo("assistant"));
        Assert.That(assistantMsg["content"]!.GetValue<string>(), Is.EqualTo("thinking"));
        Assert.That(assistantMsg.AsObject().ContainsKey("tool_calls"), Is.False);
    }

    [Test]
    public async Task RequestBody_AssistantToolCallsOnly_ContentIsNull()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.ToolCall("call-1", "click_button", "{\"id\":\"ok\"}"),
            }),
        };

        await llm.CreateTurnAsync("sys", history, Array.Empty<ToolDefinition>(), 100, default);

        var assistantMsg = handler.RequestBodies[0]!["messages"]!.AsArray()[1]!;
        Assert.That(assistantMsg["content"], Is.Null);
        var toolCalls = assistantMsg["tool_calls"]!.AsArray();
        Assert.That(toolCalls, Has.Count.EqualTo(1));
        Assert.That(toolCalls[0]!["id"]!.GetValue<string>(), Is.EqualTo("call-1"));
        Assert.That(toolCalls[0]!["type"]!.GetValue<string>(), Is.EqualTo("function"));
        Assert.That(toolCalls[0]!["function"]!["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(toolCalls[0]!["function"]!["arguments"]!.GetValue<string>(), Is.EqualTo("{\"id\":\"ok\"}"));
    }

    [Test]
    public async Task RequestBody_AssistantToolCall_BlankArgumentsDefaultToEmptyObject()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.ToolCall("call-1", "noop", ""),
            }),
        };

        await llm.CreateTurnAsync("sys", history, Array.Empty<ToolDefinition>(), 100, default);

        var toolCalls = handler.RequestBodies[0]!["messages"]!.AsArray()[1]!["tool_calls"]!.AsArray();
        Assert.That(toolCalls[0]!["function"]!["arguments"]!.GetValue<string>(), Is.EqualTo("{}"));
    }

    [Test]
    public async Task RequestBody_ToolResults_OneMessagePerResult_TiedToCallId()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("call-1", "{\"ok\":true}", false),
                new ToolResultPart("call-2", "boom", true),
            }),
        };

        await llm.CreateTurnAsync("sys", history, Array.Empty<ToolDefinition>(), 100, default);

        var messages = handler.RequestBodies[0]!["messages"]!.AsArray();
        Assert.That(messages, Has.Count.EqualTo(3)); // system + 2 tool results
        Assert.That(messages[1]!["role"]!.GetValue<string>(), Is.EqualTo("tool"));
        Assert.That(messages[1]!["tool_call_id"]!.GetValue<string>(), Is.EqualTo("call-1"));
        Assert.That(messages[1]!["content"]!.GetValue<string>(), Is.EqualTo("{\"ok\":true}"));
        Assert.That(messages[2]!["tool_call_id"]!.GetValue<string>(), Is.EqualTo("call-2"));
        Assert.That(messages[2]!["content"]!.GetValue<string>(), Is.EqualTo("boom"));
    }

    // ---- Response parsing -------------------------------------------------------------------

    [Test]
    public async Task ResponseParsing_TextOnly()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"the answer"}}]}""");
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(result.Parts, Has.Count.EqualTo(1));
        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("the answer"));
    }

    [Test]
    public async Task ResponseParsing_ToolCallsOnly()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":null,"tool_calls":[
                {"id":"call-1","type":"function","function":{"name":"click_button","arguments":"{\"id\":\"ok\"}"}}
            ]}}]}
            """);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(result.Parts, Has.Count.EqualTo(1));
        var call = (AssistantPart.ToolCall)result.Parts[0];
        Assert.That(call.Id, Is.EqualTo("call-1"));
        Assert.That(call.Name, Is.EqualTo("click_button"));
        Assert.That(call.ArgumentsJson, Is.EqualTo("{\"id\":\"ok\"}"));
    }

    [Test]
    public async Task ResponseParsing_TextAndToolCalls_BothPartsPresent()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"let me click that","tool_calls":[
                {"id":"call-1","type":"function","function":{"name":"click_button","arguments":"{}"}}
            ]}}]}
            """);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(result.Parts, Has.Count.EqualTo(2));
        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("let me click that"));
        Assert.That(((AssistantPart.ToolCall)result.Parts[1]).Name, Is.EqualTo("click_button"));
    }

    [Test]
    public async Task ResponseParsing_MissingArguments_DefaultsToEmptyObject()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """
            {"choices":[{"message":{"tool_calls":[
                {"id":"call-1","type":"function","function":{"name":"noop"}}
            ]}}]}
            """);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(((AssistantPart.ToolCall)result.Parts[0]).ArgumentsJson, Is.EqualTo("{}"));
    }

    // ---- Credentials -------------------------------------------------------------------------

    [Test]
    public void CreateTurnAsync_NoApiKey_ThrowsInvalidOperationException()
    {
        var handler = new ScriptedHttpMessageHandler();
        var llm = new OpenAiToolCallingLlm(new HttpClient(handler), NullLogger<OpenAiToolCallingLlm>.Instance, (Func<string?>)(() => null));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default));
    }

    // ---- Retry / error handling ---------------------------------------------------------------

    [Test]
    public async Task CreateTurnAsync_429ThenSuccess_RetriesAndReturnsResult()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, "rate limited", retryAfterSeconds: 0)
            .Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        var result = await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default);

        Assert.That(((AssistantPart.Text)result.Parts[0]).Value, Is.EqualTo("hello"));
        Assert.That(handler.CallCount, Is.EqualTo(2));
    }

    [Test]
    public void CreateTurnAsync_429Always_ExhaustsRetriesThenThrows()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.TooManyRequests, "rate limited", retryAfterSeconds: 0);
        var llm = MakeLlm(handler);

        Assert.ThrowsAsync<HttpRequestException>(() =>
            llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default));
        Assert.That(handler.CallCount, Is.EqualTo(6)); // initial attempt + 5 retries
    }

    [Test]
    public void CreateTurnAsync_ServerError_NotRetried_ThrowsImmediately()
    {
        // OpenAI's adapter only retries 429 — 5xx is left to the key-pool failover layer, not
        // retried in-place like Gemini does.
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.InternalServerError, "boom");
        var llm = MakeLlm(handler);

        Assert.ThrowsAsync<HttpRequestException>(() =>
            llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default));
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }

    [Test]
    public void CreateTurnAsync_BadRequest_ThrowsImmediately()
    {
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.BadRequest, "nope");
        var llm = MakeLlm(handler);

        Assert.ThrowsAsync<HttpRequestException>(() =>
            llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default));
        Assert.That(handler.CallCount, Is.EqualTo(1));
    }
}

using System.Net;
using System.Text.Json.Nodes;
using Automata.Core.Operator;
using Automata.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Covers <see cref="GeminiToolCallingLlm"/>'s wire translation — request body shape, the
/// call-id-to-function-name correlation inside <see cref="GeminiToolCallingLlm.ToGeminiContents"/>
/// (Gemini has no native call ids; this adapter synthesizes them), response parsing via
/// <see cref="GeminiToolCallingLlm.FromGeminiParts"/>, and the provider-specific retry behaviour
/// (Gemini retries both 429 and 5xx, unlike OpenAI which only retries 429 —
/// see <see cref="OpenAiToolCallingLlmTests"/>). A mapping bug here silently breaks AI step repair
/// for this provider, so shapes are checked directly rather than just asserting success.
/// </summary>
[TestFixture]
public class GeminiToolCallingLlmTests
{
    private const string TextOnlyResponse = """{"candidates":[{"content":{"parts":[{"text":"hello"}]}}]}""";

    private static GeminiToolCallingLlm MakeLlm(ScriptedHttpMessageHandler handler, string key = "key-test") =>
        new(new HttpClient(handler), NullLogger<GeminiToolCallingLlm>.Instance, () => key);

    // ---- Request body shape ----------------------------------------------------------------

    [Test]
    public async Task RequestBody_SystemPromptGoesInSystemInstruction()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        await llm.CreateTurnAsync("you are a browser operator", new[] { new ToolLoopMessage.UserText("hi") },
            Array.Empty<ToolDefinition>(), 1000, default);

        var text = handler.RequestBodies[0]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>();
        Assert.That(text, Is.EqualTo("you are a browser operator"));
    }

    [Test]
    public async Task RequestBody_FunctionDeclarationsOmittedWhenToolsEmpty()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 1000, default);

        Assert.That(handler.RequestBodies[0]!.AsObject().ContainsKey("tools"), Is.False);
    }

    [Test]
    public async Task RequestBody_FunctionDeclarationsIncludedWhenToolsPresent()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);
        var schema = new JsonObject { ["type"] = "object" };
        var tools = new[] { new ToolDefinition("click_button", "Clicks a button", schema) };

        await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, tools, 1000, default);

        var decls = handler.RequestBodies[0]!["tools"]![0]!["functionDeclarations"]!.AsArray();
        Assert.That(decls, Has.Count.EqualTo(1));
        Assert.That(decls[0]!["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(decls[0]!["description"]!.GetValue<string>(), Is.EqualTo("Clicks a button"));
        Assert.That(decls[0]!["parameters"]!["type"]!.GetValue<string>(), Is.EqualTo("object"));
    }

    [Test]
    public async Task RequestBody_MaxOutputTokensMapsFromMaxTokens()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, TextOnlyResponse);
        var llm = MakeLlm(handler);

        await llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 4096, default);

        Assert.That(handler.RequestBodies[0]!["generationConfig"]!["maxOutputTokens"]!.GetValue<int>(), Is.EqualTo(4096));
    }

    // ---- ToGeminiContents (internal — directly testable) -----------------------------------

    [Test]
    public void ToGeminiContents_UserText_MapsToUserRoleTextPart()
    {
        var contents = GeminiToolCallingLlm.ToGeminiContents(new ToolLoopMessage[] { new ToolLoopMessage.UserText("go to the page") });

        Assert.That(contents[0]!["role"]!.GetValue<string>(), Is.EqualTo("user"));
        Assert.That(contents[0]!["parts"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("go to the page"));
    }

    [Test]
    public void ToGeminiContents_AssistantText_MapsToModelRole()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart> { new AssistantPart.Text("thinking") }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        Assert.That(contents[0]!["role"]!.GetValue<string>(), Is.EqualTo("model"));
        Assert.That(contents[0]!["parts"]![0]!["text"]!.GetValue<string>(), Is.EqualTo("thinking"));
    }

    [Test]
    public void ToGeminiContents_AssistantToolCall_MapsToFunctionCallWithParsedArgs()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.ToolCall("gemini-call-0", "click_button", "{\"id\":\"ok\"}"),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var call = contents[0]!["parts"]![0]!["functionCall"]!;
        Assert.That(call["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
        Assert.That(call["args"]!["id"]!.GetValue<string>(), Is.EqualTo("ok"));
    }

    [Test]
    public void ToGeminiContents_AssistantToolCall_BlankArgumentsDefaultToEmptyObject()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.ToolCall("gemini-call-0", "noop", ""),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var args = contents[0]!["parts"]![0]!["functionCall"]!["args"]!.AsObject();
        Assert.That(args, Is.Empty);
    }

    [Test]
    public void ToGeminiContents_ToolResults_CorrelatesBackToFunctionNameByCallId()
    {
        // The call id is synthesized by this adapter (Gemini has none natively); ToGeminiContents
        // must remember which name each id belongs to from the preceding AssistantTurn so the
        // functionResponse carries the right name instead of the opaque id.
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.AssistantTurn(new List<AssistantPart>
            {
                new AssistantPart.ToolCall("gemini-call-0", "click_button", "{}"),
                new AssistantPart.ToolCall("gemini-call-1", "read_text", "{}"),
            }),
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("gemini-call-1", "{\"text\":\"hi\"}", false),
                new ToolResultPart("gemini-call-0", "{\"ok\":true}", false),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var resultContent = contents[1]!;
        Assert.That(resultContent["role"]!.GetValue<string>(), Is.EqualTo("user"));
        var parts = resultContent["parts"]!.AsArray();
        Assert.That(parts[0]!["functionResponse"]!["name"]!.GetValue<string>(), Is.EqualTo("read_text"));
        Assert.That(parts[1]!["functionResponse"]!["name"]!.GetValue<string>(), Is.EqualTo("click_button"));
    }

    [Test]
    public void ToGeminiContents_ToolResult_UnknownCallId_FallsBackToIdAsName()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("unmapped-id", "{}", false),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        Assert.That(contents[0]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>(), Is.EqualTo("unmapped-id"));
    }

    // ---- WrapResponse (internal — directly testable) ---------------------------------------

    [Test]
    public void WrapResponse_JsonObjectContent_ReturnedAsIs()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("call-1", "{\"ok\":true,\"count\":3}", false),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var response = contents[0]!["parts"]![0]!["functionResponse"]!["response"]!;
        Assert.That(response["ok"]!.GetValue<bool>(), Is.True);
        Assert.That(response["count"]!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(response.AsObject().ContainsKey("result"), Is.False);
    }

    [Test]
    public void WrapResponse_NonObjectJsonContent_WrappedUnderResultKey()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("call-1", "[1,2,3]", false),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var response = contents[0]!["parts"]![0]!["functionResponse"]!["response"]!;
        Assert.That(response["result"]!.AsArray().Select(n => n!.GetValue<int>()), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void WrapResponse_NonJsonStringContent_WrappedUnderResultKey()
    {
        var history = new ToolLoopMessage[]
        {
            new ToolLoopMessage.ToolResults(new List<ToolResultPart>
            {
                new ToolResultPart("call-1", "plain text, not json", false),
            }),
        };

        var contents = GeminiToolCallingLlm.ToGeminiContents(history);

        var response = contents[0]!["parts"]![0]!["functionResponse"]!["response"]!;
        Assert.That(response["result"]!.GetValue<string>(), Is.EqualTo("plain text, not json"));
    }

    // ---- FromGeminiParts (internal — directly testable) ------------------------------------

    [Test]
    public void FromGeminiParts_TextPart_MapsToAssistantText()
    {
        var parts = new JsonArray { new JsonObject { ["text"] = "the answer" } };

        var result = GeminiToolCallingLlm.FromGeminiParts(parts);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(((AssistantPart.Text)result[0]).Value, Is.EqualTo("the answer"));
    }

    [Test]
    public void FromGeminiParts_FunctionCallPart_SynthesizesIdAndCarriesArgsAsJsonString()
    {
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["functionCall"] = new JsonObject { ["name"] = "click_button", ["args"] = new JsonObject { ["id"] = "ok" } },
            },
        };

        var result = GeminiToolCallingLlm.FromGeminiParts(parts);

        var call = (AssistantPart.ToolCall)result[0];
        Assert.That(call.Id, Is.EqualTo("gemini-call-0"));
        Assert.That(call.Name, Is.EqualTo("click_button"));
        Assert.That(call.ArgumentsJson, Is.EqualTo("{\"id\":\"ok\"}"));
    }

    [Test]
    public void FromGeminiParts_MultipleFunctionCalls_SynthesizeDistinctSequentialIds()
    {
        var parts = new JsonArray
        {
            new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "a", ["args"] = new JsonObject() } },
            new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "b", ["args"] = new JsonObject() } },
        };

        var result = GeminiToolCallingLlm.FromGeminiParts(parts);

        Assert.That(((AssistantPart.ToolCall)result[0]).Id, Is.EqualTo("gemini-call-0"));
        Assert.That(((AssistantPart.ToolCall)result[1]).Id, Is.EqualTo("gemini-call-1"));
    }

    [Test]
    public void FromGeminiParts_TextAndFunctionCall_BothPartsPresent()
    {
        var parts = new JsonArray
        {
            new JsonObject { ["text"] = "let me click that" },
            new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "click_button", ["args"] = new JsonObject() } },
        };

        var result = GeminiToolCallingLlm.FromGeminiParts(parts);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(((AssistantPart.Text)result[0]).Value, Is.EqualTo("let me click that"));
        Assert.That(((AssistantPart.ToolCall)result[1]).Name, Is.EqualTo("click_button"));
    }

    // ---- Credentials -------------------------------------------------------------------------

    [Test]
    public async Task IsConfiguredAsync_ReflectsKeyResolver()
    {
        using var http = new HttpClient();
        var log = NullLogger<GeminiToolCallingLlm>.Instance;

        Assert.That(await new GeminiToolCallingLlm(http, log, () => "AIza-test").IsConfiguredAsync(), Is.True);
        Assert.That(await new GeminiToolCallingLlm(http, log, (Func<string?>)(() => null)).IsConfiguredAsync(), Is.False);
    }

    [Test]
    public void CreateTurnAsync_NoApiKey_ThrowsInvalidOperationException()
    {
        var handler = new ScriptedHttpMessageHandler();
        var llm = new GeminiToolCallingLlm(new HttpClient(handler), NullLogger<GeminiToolCallingLlm>.Instance, (Func<string?>)(() => null));

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            llm.CreateTurnAsync("sys", new[] { new ToolLoopMessage.UserText("hi") }, Array.Empty<ToolDefinition>(), 100, default));
    }

    // ---- Response edge cases -------------------------------------------------------------------

    [Test]
    public void CreateTurnAsync_NoCandidates_ThrowsInvalidOperationException()
    {
        var handler = new ScriptedHttpMessageHandler().Respond(HttpStatusCode.OK, """{"candidates":[]}""");
        var llm = MakeLlm(handler);

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
    public async Task CreateTurnAsync_ServerErrorThenSuccess_RetriesAndReturnsResult()
    {
        // Unlike OpenAI's adapter, Gemini's also retries 5xx in place.
        var handler = new ScriptedHttpMessageHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, "overloaded", retryAfterSeconds: 0)
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

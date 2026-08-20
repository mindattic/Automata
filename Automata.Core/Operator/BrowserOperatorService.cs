using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Automata.Core.Operator;

/// <summary>
/// Generic tool-calling loop that drives a live browser pane toward a plain-English task, using
/// whichever <see cref="IToolCallingLlm"/> provider has usable credentials right now (tried in
/// preference order — Claude first, per the Multi-LLM Master Switch-Over fallback philosophy).
///
/// This is the loop mechanics lifted wholesale out of Prose.KdpPublish's <c>KdpOperatorService</c>
/// — that type had zero KDP-specific code in its own loop; only its system prompts and pre-flight
/// hard gates were KDP business logic, both of which the caller now supplies (or omits) instead
/// of this class hard-coding them.
/// </summary>
public class BrowserOperatorService
{
    private readonly IReadOnlyList<IToolCallingLlm> toolCallingProviders;
    private readonly BrowserToolRegistry tools;
    private readonly ILogger<BrowserOperatorService> log;

    private const int MaxTokens = 4096;
    private const int DefaultMaxToolIterations = 40;

    public BrowserOperatorService(IReadOnlyList<IToolCallingLlm> toolCallingProviders, BrowserToolRegistry tools, ILogger<BrowserOperatorService> log)
    {
        this.toolCallingProviders = toolCallingProviders;
        this.tools = tools;
        this.log = log;
    }

    /// <summary>
    /// Drive one task to completion (or the iteration cap) against <paramref name="ctx"/>'s
    /// live browser surface, using the full generic tool set from <see cref="BrowserToolRegistry"/>.
    /// </summary>
    public async IAsyncEnumerable<OperatorEvent> RunAsync(
        string systemPrompt,
        string userMessage,
        BrowserOperatorContext ctx,
        int maxIterations = DefaultMaxToolIterations,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        IToolCallingLlm? llm = null;
        foreach (var candidate in toolCallingProviders)
        {
            if (await candidate.IsConfiguredAsync())
            {
                llm = candidate;
                break;
            }
        }
        if (llm is null)
        {
            yield return new OperatorEvent.Error(
                "No tool-calling LLM provider is configured (tried: " +
                string.Join(", ", toolCallingProviders.Select(p => p.Name)) + ").");
            yield break;
        }
        log.LogInformation("BrowserOperatorService: using {Provider}", llm.Name);

        var history = new List<ToolLoopMessage> { new ToolLoopMessage.UserText(userMessage) };
        var toolDefs = tools.BuildToolDefinitions();

        for (int iter = 0; iter < maxIterations; iter++)
        {
            cancel.ThrowIfCancellationRequested();

            ToolTurnResult? turn = null;
            string? callError = null;
            try
            {
                turn = await llm.CreateTurnAsync(systemPrompt, history, toolDefs, MaxTokens, cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "{Provider} call failed", llm.Name);
                callError = ex.Message;
            }
            if (turn == null)
            {
                yield return new OperatorEvent.Error(callError ?? $"{llm.Name} call returned null.");
                yield break;
            }

            history.Add(new ToolLoopMessage.AssistantTurn(turn.Parts));

            var toolResults = new List<ToolResultPart>();
            var sawToolUse = false;

            foreach (var part in turn.Parts)
            {
                if (part is AssistantPart.Text textPart)
                {
                    if (!string.IsNullOrEmpty(textPart.Value))
                        yield return new OperatorEvent.AssistantText(textPart.Value);
                }
                else if (part is AssistantPart.ToolCall call)
                {
                    sawToolUse = true;
                    var name = call.Name;
                    var argsJson = call.ArgumentsJson;

                    yield return new OperatorEvent.ToolStarted(name, argsJson);

                    string resultJson;
                    bool isError = false;
                    try
                    {
                        var tool = tools.Get(name) ?? throw new InvalidOperationException($"Unknown tool: {name}");
                        using var argsDoc = JsonDocument.Parse(argsJson);
                        resultJson = await tool.InvokeAsync(argsDoc.RootElement, ctx, cancel);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.LogError(ex, "Tool {Tool} threw", name);
                        resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                    }

                    yield return new OperatorEvent.ToolCompleted(name, resultJson, isError);
                    toolResults.Add(new ToolResultPart(call.Id, resultJson, isError));
                }
            }

            if (!sawToolUse) yield break;

            history.Add(new ToolLoopMessage.ToolResults(toolResults));
        }

        yield return new OperatorEvent.Error(
            $"Tool-use loop hit the {maxIterations}-iteration safety cap without finishing.");
    }
}

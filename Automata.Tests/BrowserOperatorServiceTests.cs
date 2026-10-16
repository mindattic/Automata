using System.Text.Json;
using Automata.Core.Operator;
using Automata.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Automata.Tests;

[TestFixture]
public class BrowserOperatorServiceTests
{
    /// <summary>Scripted IToolCallingLlm: dequeues one turn-producing func per call (each sees the
    /// history so far), optionally repeating the last one forever to drive the iteration cap.</summary>
    private sealed class StubLlm : IToolCallingLlm
    {
        private readonly Queue<Func<IReadOnlyList<ToolLoopMessage>, ToolTurnResult>> turns;
        private readonly bool configured;
        private readonly bool repeatLast;

        public StubLlm(string name, bool configured, bool repeatLast,
            params Func<IReadOnlyList<ToolLoopMessage>, ToolTurnResult>[] turns)
        {
            Name = name;
            this.configured = configured;
            this.repeatLast = repeatLast;
            this.turns = new Queue<Func<IReadOnlyList<ToolLoopMessage>, ToolTurnResult>>(turns);
        }

        public string Name { get; }
        public Task<bool> IsConfiguredAsync() => Task.FromResult(configured);

        public Task<ToolTurnResult> CreateTurnAsync(
            string systemPrompt,
            IReadOnlyList<ToolLoopMessage> history,
            IReadOnlyList<ToolDefinition> tools,
            int maxTokens,
            CancellationToken ct)
        {
            var next = repeatLast && turns.Count == 1 ? turns.Peek() : turns.Dequeue();
            return Task.FromResult(next(history));
        }
    }

    private sealed class ThrowingLlm : IToolCallingLlm
    {
        public string Name => "Thrower";
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);

        public Task<ToolTurnResult> CreateTurnAsync(
            string systemPrompt, IReadOnlyList<ToolLoopMessage> history,
            IReadOnlyList<ToolDefinition> tools, int maxTokens, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private sealed class NullReturningLlm : IToolCallingLlm
    {
        public string Name => "NullBot";
        public Task<bool> IsConfiguredAsync() => Task.FromResult(true);

        public Task<ToolTurnResult> CreateTurnAsync(
            string systemPrompt, IReadOnlyList<ToolLoopMessage> history,
            IReadOnlyList<ToolDefinition> tools, int maxTokens, CancellationToken ct)
            => Task.FromResult<ToolTurnResult>(null!);
    }

    private sealed class EchoTool : IBrowserTool
    {
        public string Name => "echo";
        public string Description => "Echoes a fixed result.";
        public string ParametersJsonSchema => """{ "type": "object" }""";

        public Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext context, CancellationToken ct)
            => Task.FromResult("""{ "echoed": true }""");
    }

    private sealed class ThrowingTool : IBrowserTool
    {
        public string Name => "throwing_tool";
        public string Description => "Always throws.";
        public string ParametersJsonSchema => """{ "type": "object" }""";

        public Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext context, CancellationToken ct)
            => throw new InvalidOperationException("tool boom");
    }

    private static async Task<List<OperatorEvent>> RunToEnd(
        BrowserOperatorService svc, int maxIterations = 40)
    {
        var events = new List<OperatorEvent>();
        var ctx = new BrowserOperatorContext { Browser = new FakeBrowserSurface() };
        await foreach (var e in svc.RunAsync("system", "go", ctx, maxIterations))
            events.Add(e);
        return events;
    }

    [Test]
    public async Task PicksFirstProvider_WhoseIsConfiguredAsyncIsTrue()
    {
        var a = new StubLlm("A", configured: false, repeatLast: false,
            _ => throw new InvalidOperationException("A should never be called"));
        var b = new StubLlm("B", configured: true, repeatLast: false,
            _ => new ToolTurnResult([new AssistantPart.Text("from B")]));
        var c = new StubLlm("C", configured: true, repeatLast: false,
            _ => throw new InvalidOperationException("C should never be called"));
        var svc = new BrowserOperatorService(
            [a, b, c], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        var text = (OperatorEvent.AssistantText)events[0];
        Assert.That(text.Text, Is.EqualTo("from B"));
    }

    [Test]
    public async Task NoProviderConfigured_YieldsError_ListingTriedNames()
    {
        var a = new StubLlm("Alpha", configured: false, repeatLast: false);
        var b = new StubLlm("Beta", configured: false, repeatLast: false);
        var svc = new BrowserOperatorService(
            [a, b], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        var error = (OperatorEvent.Error)events[0];
        Assert.That(error.Message, Does.Contain("Alpha"));
        Assert.That(error.Message, Does.Contain("Beta"));
    }

    [Test]
    public async Task TextOnlyTurn_EndsTheLoop()
    {
        var llm = new StubLlm("Solo", configured: true, repeatLast: false,
            _ => new ToolTurnResult([new AssistantPart.Text("hello")]));
        var svc = new BrowserOperatorService(
            [llm], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc, maxIterations: 1);

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.InstanceOf<OperatorEvent.AssistantText>());
    }

    [Test]
    public async Task ToolCall_EmitsStartedAndCompleted_AndAppendsResultToHistory()
    {
        List<ToolLoopMessage>? secondTurnHistory = null;
        var llm = new StubLlm("Tooler", configured: true, repeatLast: false,
            _ => new ToolTurnResult([new AssistantPart.ToolCall("t1", "echo", """{ "x": 1 }""")]),
            history =>
            {
                secondTurnHistory = history.ToList();
                return new ToolTurnResult([new AssistantPart.Text("done")]);
            });
        var svc = new BrowserOperatorService(
            [llm], new BrowserToolRegistry([new EchoTool()]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        var started = events.OfType<OperatorEvent.ToolStarted>().Single();
        Assert.That(started.Name, Is.EqualTo("echo"));
        Assert.That(started.ArgsJson, Is.EqualTo("""{ "x": 1 }"""));

        var completed = events.OfType<OperatorEvent.ToolCompleted>().Single();
        Assert.That(completed.Name, Is.EqualTo("echo"));
        Assert.That(completed.IsError, Is.False);
        Assert.That(completed.ResultJson, Does.Contain("echoed"));

        Assert.That(secondTurnHistory, Is.Not.Null);
        var toolResults = secondTurnHistory!.OfType<ToolLoopMessage.ToolResults>().Single();
        var result = toolResults.Results.Single();
        Assert.That(result.ToolCallId, Is.EqualTo("t1"));
        Assert.That(result.IsError, Is.False);
        Assert.That(result.Content, Does.Contain("echoed"));
    }

    [Test]
    public async Task UnknownToolName_YieldsErrorResult_WithoutAbortingTheLoop()
    {
        var llm = new StubLlm("Unknowner", configured: true, repeatLast: false,
            _ => new ToolTurnResult([new AssistantPart.ToolCall("t1", "does_not_exist", "{}")]),
            _ => new ToolTurnResult([new AssistantPart.Text("done")]));
        var svc = new BrowserOperatorService(
            [llm], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        var completed = events.OfType<OperatorEvent.ToolCompleted>().Single();
        Assert.That(completed.IsError, Is.True);
        Assert.That(completed.ResultJson, Does.Contain("Unknown tool"));
        Assert.That(events.OfType<OperatorEvent.AssistantText>().Single().Text, Is.EqualTo("done"));
    }

    [Test]
    public async Task ToolThatThrows_YieldsErrorResult_WithoutAbortingTheLoop()
    {
        var llm = new StubLlm("Thrower", configured: true, repeatLast: false,
            _ => new ToolTurnResult([new AssistantPart.ToolCall("t1", "throwing_tool", "{}")]),
            _ => new ToolTurnResult([new AssistantPart.Text("done")]));
        var svc = new BrowserOperatorService(
            [llm], new BrowserToolRegistry([new ThrowingTool()]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        var completed = events.OfType<OperatorEvent.ToolCompleted>().Single();
        Assert.That(completed.IsError, Is.True);
        Assert.That(completed.ResultJson, Does.Contain("tool boom"));
        Assert.That(events.OfType<OperatorEvent.AssistantText>().Single().Text, Is.EqualTo("done"));
    }

    [Test]
    public async Task LlmCallThatThrows_YieldsError()
    {
        var svc = new BrowserOperatorService(
            [new ThrowingLlm()], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        var error = (OperatorEvent.Error)events[0];
        Assert.That(error.Message, Does.Contain("boom"));
    }

    [Test]
    public async Task LlmCallThatReturnsNull_YieldsError()
    {
        var svc = new BrowserOperatorService(
            [new NullReturningLlm()], new BrowserToolRegistry([]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc);

        Assert.That(events, Has.Count.EqualTo(1));
        var error = (OperatorEvent.Error)events[0];
        Assert.That(error.Message, Does.Contain("call returned null"));
    }

    [Test]
    public async Task HittingMaxIterations_YieldsSafetyCapError()
    {
        var llm = new StubLlm("Looper", configured: true, repeatLast: true,
            _ => new ToolTurnResult([new AssistantPart.ToolCall("t1", "echo", "{}")]));
        var svc = new BrowserOperatorService(
            [llm], new BrowserToolRegistry([new EchoTool()]), NullLogger<BrowserOperatorService>.Instance);

        var events = await RunToEnd(svc, maxIterations: 3);

        Assert.That(events.OfType<OperatorEvent.ToolStarted>().Count(), Is.EqualTo(3));
        var last = events[^1];
        Assert.That(last, Is.InstanceOf<OperatorEvent.Error>());
        Assert.That(((OperatorEvent.Error)last).Message, Does.Contain("3-iteration safety cap"));
    }
}

using Automata.Core.Operator;

namespace Automata.Tests.Fakes;

/// <summary>
/// Scriptable IBrowserSurface for engine tests: EvalAsync answers come from a queue of
/// script-inspecting responders (falling back to <see cref="DefaultEvalResponse"/>), and every
/// call is recorded for assertions on what the engine actually did (or didn't do).
/// </summary>
public sealed class FakeBrowserSurface : IBrowserSurface
{
    public string CurrentUrl { get; set; } = "about:blank";

    /// <summary>Every surface call in order: (method, argument summary).</summary>
    public List<(string Method, string Args)> Calls { get; } = [];

    /// <summary>Dequeued one per EvalAsync call; each receives the script and returns the JS result.</summary>
    public Queue<Func<string, string>> EvalResponses { get; } = new();

    /// <summary>Used when the queue is empty. Defaults to "{}".</summary>
    public Func<string, string> DefaultEvalResponse { get; set; } = _ => "{}";

    public Task NavigateAsync(string url, CancellationToken ct)
    {
        Calls.Add(("Navigate", url));
        CurrentUrl = url;
        return Task.CompletedTask;
    }

    public Task<string> EvalAsync(string script, CancellationToken ct)
    {
        Calls.Add(("Eval", script));
        var responder = EvalResponses.Count > 0 ? EvalResponses.Dequeue() : DefaultEvalResponse;
        return Task.FromResult(responder(script));
    }

    public Task InjectFileAsync(string filePath, string selector, CancellationToken ct)
    {
        Calls.Add(("InjectFile", $"{filePath} -> {selector}"));
        return Task.CompletedTask;
    }

    public Task ClickAtPointAsync(double x, double y, CancellationToken ct)
    {
        Calls.Add(("ClickAtPoint", $"{x},{y}"));
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        Calls.Add(("TypeText", text));
        return Task.CompletedTask;
    }

    public Task PressEnterAsync(CancellationToken ct)
    {
        Calls.Add(("PressEnter", ""));
        return Task.CompletedTask;
    }
}

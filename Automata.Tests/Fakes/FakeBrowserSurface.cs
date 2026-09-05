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

    /// <summary>The read-back a TypeText step makes after typing, recognised by its script.</summary>
    private const string TypedValueProbe = "el ? el.value : null";

    public Task<string> EvalAsync(string script, CancellationToken ct)
    {
        Calls.Add(("Eval", script));
        if (EvalResponses.Count == 0 && script.Contains(TypedValueProbe))
        {
            // A real field holds what was typed into it. Answering from Calls rather than making
            // every test spell this out keeps a TypeText step's failure meaning what it says.
            return Task.FromResult(
                System.Text.Json.JsonSerializer.Serialize(new { ok = true, value = LastTyped }));
        }
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

    /// <summary>
    /// What was typed most recently. A real field holds what was typed into it, and the engine
    /// reads it back to check the typing landed — so a fake that forgets makes every TypeText step
    /// fail for a reason that has nothing to do with the test.
    /// </summary>
    public string? LastTyped { get; private set; }

    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        Calls.Add(("TypeText", text));
        LastTyped = text;
        return Task.CompletedTask;
    }

    public Task PressEnterAsync(CancellationToken ct)
    {
        Calls.Add(("PressEnter", ""));
        return Task.CompletedTask;
    }

    /// <summary>What a zoom reports back. Defaults to obeying; set it to something else to
    /// stand in for a page that refuses to change size.</summary>
    public Func<double, double> ZoomResponse { get; set; } = factor => factor;

    public Task<double> SetZoomAsync(double factor, CancellationToken ct)
    {
        Calls.Add(("SetZoom", factor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        return Task.FromResult(ZoomResponse(factor));
    }
}

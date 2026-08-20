using System.Text.Json;

namespace Automata.Core.Operator.Tools;

/// <summary>
/// A narrative progress note distinct from the mechanical tool_use trail already visible in the
/// event stream (click_button, upload_file, etc. each already surface their own args/result).
/// Use this for things worth telling the human that aren't tied to one mechanical action. Ported
/// verbatim from Prose.KdpPublish's <c>LogNoteTool</c>.
/// </summary>
public class LogNoteTool : IBrowserTool
{
    public string Name => "log_note";

    public string Description =>
        "Write a short progress note to the visible log — for context or decisions that " +
        "aren't captured by another tool call's own result (e.g. why a step was skipped, a " +
        "summary before moving on).";

    public string ParametersJsonSchema => """
    {
      "type": "object",
      "properties": {
        "message": { "type": "string" }
      },
      "required": ["message"]
    }
    """;

    public Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext ctx, CancellationToken ct)
    {
        // The note itself reaches the UI via the operator loop's ToolStarted/ToolCompleted event
        // stream (every tool call's args are shown) — no separate side channel needed.
        return Task.FromResult(JsonSerializer.Serialize(new { logged = true }));
    }
}

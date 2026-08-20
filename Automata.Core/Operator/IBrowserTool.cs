using System.Text.Json;

namespace Automata.Core.Operator;

/// <summary>
/// One callable surface the browser-automation operator LLM can invoke. Ported from
/// Prose.KdpPublish's <c>IKdpTool</c> — same registry/loop pattern, generalized to any website
/// instead of one specific site.
/// </summary>
public interface IBrowserTool
{
    string Name { get; }
    string Description { get; }
    string ParametersJsonSchema { get; }

    Task<string> InvokeAsync(JsonElement args, BrowserOperatorContext context, CancellationToken ct);
}

/// <summary>Per-turn context handed to every browser tool: the live browser surface to act on.</summary>
public sealed class BrowserOperatorContext
{
    public required IBrowserSurface Browser { get; init; }
}

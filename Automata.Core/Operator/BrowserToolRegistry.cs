using System.Text.Json.Nodes;

namespace Automata.Core.Operator;

/// <summary>
/// Collects every <see cref="IBrowserTool"/> registered with DI into a single addressable
/// surface. Ported from Prose.KdpPublish's <c>KdpToolRegistry</c>.
/// </summary>
public class BrowserToolRegistry
{
    private readonly Dictionary<string, IBrowserTool> byName;

    public BrowserToolRegistry(IEnumerable<IBrowserTool> tools)
    {
        byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IBrowserTool> All => byName.Values;

    public IBrowserTool? Get(string name) => byName.TryGetValue(name, out var t) ? t : null;

    /// <summary>Provider-neutral tool catalog for <see cref="IToolCallingLlm"/> implementations —
    /// each vendor adapter re-nests the same JSON Schema under its own wire envelope.</summary>
    public IReadOnlyList<ToolDefinition> BuildToolDefinitions() =>
        byName.Values.Select(t => new ToolDefinition(
            t.Name,
            t.Description,
            JsonNode.Parse(t.ParametersJsonSchema)
                ?? throw new InvalidOperationException($"Tool {t.Name}: ParametersJsonSchema is not valid JSON")))
            .ToList();
}

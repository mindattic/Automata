using System.Text.Json;
using System.Text.Json.Serialization;

namespace Automata.Core.Automation.Model;

/// <summary>
/// The one serializer configuration every on-disk Automata JSON file (collections, tasks,
/// export manifests) goes through, so a task written by one machine always reads on another.
/// </summary>
public static class AutomataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

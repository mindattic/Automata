using System.Text.Json;
using System.Text.Json.Serialization;

namespace Automata.Core.Automation.Model;

/// <summary>
/// The one serializer configuration every Automata JSON document (collections, tasks, export
/// files, and the JSON columns of the database) goes through, so a task written by one machine
/// always reads on another.
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

    /// <summary>The same shape on one line — what the database's JSON columns hold.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

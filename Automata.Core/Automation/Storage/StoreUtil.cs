using System.IO;
using System.Text.Json;
using Automata.Core.Automation.Model;

namespace Automata.Core.Automation.Storage;

/// <summary>Shared helpers for the store and the zip archive service.</summary>
internal static class StoreUtil
{
    /// <summary>Deep-clone via the canonical JSON round-trip — guarantees clone == what disk would give back.</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, AutomataJson.Options), AutomataJson.Options)!;

    public static string NewId() => Guid.NewGuid().ToString("n");

    /// <summary>Fresh ids for a whole step tree (step ids are only unique within a task).</summary>
    public static void RegenerateStepIds(IEnumerable<Step> steps)
    {
        foreach (var step in Step.Flatten(steps)) step.Id = NewId();
    }

    /// <summary>"Search Google" → "search-google"; safe for file names.</summary>
    public static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "unnamed" : slug.Length <= 60 ? slug : slug[..60];
    }

    /// <summary>
    /// Keeps a name usable as a single file name without slugging it — unlike <see cref="Slug"/>
    /// this preserves case and the extension, which matters for a dataset called "bought.csv".
    /// </summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "unnamed" : cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    /// <summary>"Work" taken → "Work (2)", then "Work (3)", … Case-insensitive.</summary>
    public static string UniqueName(string desired, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired)) return desired;
        for (var n = 2; ; n++)
        {
            var candidate = $"{desired} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

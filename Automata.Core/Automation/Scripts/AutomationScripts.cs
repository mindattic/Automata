using System.IO;
using System.Reflection;

namespace Automata.Core.Automation;

/// <summary>
/// Loads the JS building blocks embedded in this assembly. fingerprint.js and resolver.js ship
/// inside Automata.Core (not the App's wwwroot) because the replay engine is host-agnostic —
/// any IBrowserSurface implementation gets the same scripts.
/// </summary>
public static class AutomationScripts
{
    public static string FingerprintJs { get; } = Load("fingerprint.js");
    public static string ResolverJs { get; } = Load("resolver.js");

    private static string Load(string fileName)
    {
        var resource = $"Automata.Core.Automation.Scripts.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded script '{resource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

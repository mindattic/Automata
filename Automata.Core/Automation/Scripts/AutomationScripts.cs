using System.IO;
using System.Reflection;

namespace Automata.Core.Automation;

/// <summary>
/// Loads the JS building blocks embedded in this assembly. fingerprint.js and resolver.js ship
/// inside Automata.Core (not the App's wwwroot) because the replay engine is host-agnostic —
/// any IBrowserSurface implementation gets the same scripts.
/// <para>
/// Most of them can be injected on demand, once per call, and are. Two cannot, and
/// <see cref="DocumentStartJs"/> is where they go.
/// </para>
/// </summary>
public static class AutomationScripts
{
    /// <summary>Prepended to every other script here: both of them ask it the same question,
    /// and answering it twice is how the two copies drifted apart.</summary>
    public static string StabilityJs { get; } = Load("stability.js");

    public static string FingerprintJs { get; } = Load("fingerprint.js");
    public static string ResolverJs { get; } = Load("resolver.js");
    public static string HarvestJs { get; } = Load("harvest.js");

    /// <summary>
    /// Records every CLOSED shadow root the page opens. Useless unless it runs BEFORE the page's own
    /// script — see <c>DocumentStartJs</c>.
    /// </summary>
    public static string ClosedRootsJs { get; } = Load("closed.js");

    /// <summary>Talks to the copy of the resolver running inside a cross-origin frame.</summary>
    public static string FramesJs { get; } = Load("frames.js");

    /// <summary>
    /// The bundle a host installs at document-creation time, in every frame.
    /// <para>
    /// Two of these have to be there before the page runs, and for different reasons.
    /// <c>closed.js</c> can only see a closed shadow root at the instant it is created, so arriving
    /// after the page's own script means arriving after every root it built. And the resolver has to
    /// be inside a CROSS-ORIGIN frame already, because nothing outside can put it there — which is
    /// the whole mechanism frames.js depends on.
    /// </para>
    /// <para>
    /// Ordering is the file order: the registry first, then stability, which fingerprint.js reads,
    /// then the resolver, then the bridge that calls into it.
    /// </para>
    /// </summary>
    public static string DocumentStartJs { get; } = string.Join(
        Environment.NewLine, ClosedRootsJs, StabilityJs, FingerprintJs, ResolverJs, FramesJs);

    private static string Load(string fileName)
    {
        var resource = $"Automata.Core.Automation.Scripts.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded script '{resource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

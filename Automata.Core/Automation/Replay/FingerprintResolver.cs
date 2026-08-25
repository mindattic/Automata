using System.Text.Json;
using Automata.Core.Automation.Model;
using Automata.Core.Operator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Automata.Core.Automation.Replay;

/// <summary>Outcome of resolving one fingerprint against the live DOM.</summary>
public sealed record ResolveResult(
    bool Found,
    bool Unique,
    string? Strategy,
    double Score,
    bool Ambiguous,
    int CandidateCount,
    double CenterX,
    double CenterY,
    string? Text,
    ElementFingerprint? Refreshed);

/// <summary>
/// Injects resolver.js (+ fingerprint.js for self-heal re-fingerprinting) and runs the cascade.
/// Polls until the element resolves or the step's timeout elapses — late-rendering SPA elements
/// are the norm, not the exception. A successful resolve leaves the element on
/// <c>window.__automataLastResolved</c> for the follow-up act script.
/// </summary>
public class FingerprintResolver
{
    private readonly ILogger<FingerprintResolver> log;

    /// <summary>Delay between resolve attempts. Lowered in tests to keep them fast.</summary>
    public int PollIntervalMs { get; init; } = 500;

    public FingerprintResolver(ILogger<FingerprintResolver>? log = null)
        => this.log = log ?? NullLogger<FingerprintResolver>.Instance;

    public async Task<ResolveResult> ResolveAsync(
        IBrowserSurface browser,
        ElementFingerprint fingerprint,
        bool highlight,
        bool refingerprint,
        int timeoutMs,
        CancellationToken ct)
    {
        var script = BuildScript(fingerprint, highlight, refingerprint);
        var started = Environment.TickCount64;
        ResolveResult result;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            result = Parse(await browser.EvalAsync(script, ct));
            if (result.Found) return result;

            var elapsed = Environment.TickCount64 - started;
            if (elapsed + PollIntervalMs > timeoutMs) break;
            await Task.Delay(PollIntervalMs, ct);
        }

        log.LogWarning("Resolve failed after {Timeout}ms: ambiguous={Ambiguous} candidates={Count}",
            timeoutMs, result.Ambiguous, result.CandidateCount);
        return result;
    }

    private static string BuildScript(ElementFingerprint fingerprint, bool highlight, bool refingerprint)
    {
        var fpJson = JsonSerializer.Serialize(fingerprint, AutomataJson.Options);
        var optsJson = $"{{ \"highlight\": {(highlight ? "true" : "false")}, \"refingerprint\": {(refingerprint ? "true" : "false")} }}";
        return $$"""
        (function() {
        {{AutomationScripts.FingerprintJs}}
        {{AutomationScripts.ResolverJs}}
        return window.__automataResolve({{fpJson}}, {{optsJson}});
        })()
        """;
    }

    private static ResolveResult Parse(string raw)
    {
        Envelope? env = null;
        try { env = JsonSerializer.Deserialize<Envelope>(raw, AutomataJson.Options); }
        catch (JsonException) { /* page returned garbage — treated as not found */ }
        env ??= new Envelope();

        return new ResolveResult(
            env.Found, env.Unique, env.Strategy, env.Score, env.Ambiguous, env.CandidateCount,
            env.CenterX, env.CenterY, env.Text, env.RefreshedFingerprint);
    }

    private sealed class Envelope
    {
        public bool Found { get; set; }
        public bool Unique { get; set; }
        public string? Strategy { get; set; }
        public double Score { get; set; }
        public bool Ambiguous { get; set; }
        public int CandidateCount { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public string? Text { get; set; }
        public ElementFingerprint? RefreshedFingerprint { get; set; }
    }
}

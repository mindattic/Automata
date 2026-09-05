using Automata.Core.Operator;

namespace Automata.Tests.Fakes;

/// <summary>
/// Hands out a fresh <see cref="FakeBrowserSurface"/> per browser and records what was asked for,
/// so a test can assert profile isolation and how many browsers a run actually opened.
/// </summary>
public sealed class FakeBrowserSurfaceFactory : IBrowserSurfaceFactory
{
    /// <summary>Profile keys requested, in order — one entry per browser actually created.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Every browser handed out, so a test can inspect what ran where.</summary>
    public List<FakeSession> Sessions { get; } = [];

    /// <summary>
    /// Shapes each new surface's eval responses. The default answers the probes every run makes -
    /// the page-busy check and the resolver - so a browser behaves like a working one unless a
    /// test deliberately makes it behave otherwise.
    /// </summary>
    public Func<string, string> Responder { get; set; } = script =>
    {
        if (script.Contains("isProcessing")) return """{ "isProcessing": false }""";
        if (script.Contains("__automataResolve("))
        {
            return """
                { "found": true, "unique": true, "strategy": "css", "ambiguous": false,
                  "candidateCount": 1, "centerX": 10, "centerY": 20, "tag": "input", "text": "x" }
                """;
        }
        return "{}";
    };

    public Task<IBrowserSession> CreateAsync(string profileKey, CancellationToken ct)
    {
        lock (Requested)
        {
            Requested.Add(profileKey);
            var session = new FakeSession(profileKey, new FakeBrowserSurface { DefaultEvalResponse = Responder });
            Sessions.Add(session);
            return Task.FromResult<IBrowserSession>(session);
        }
    }

    public sealed class FakeSession(string profileKey, FakeBrowserSurface surface) : IBrowserSession
    {
        public string ProfileKey => profileKey;
        public IBrowserSurface Surface => surface;
        public FakeBrowserSurface Fake => surface;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

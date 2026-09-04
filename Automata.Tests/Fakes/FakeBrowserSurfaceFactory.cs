using Automata.Core.Operator;

namespace Automata.Tests.Fakes;

/// <summary>
/// Hands out a fresh <see cref="FakeBrowserSurface"/> per lane and records what was asked for, so a
/// test can assert profile isolation and how many browsers a run actually opened.
/// </summary>
public sealed class FakeBrowserSurfaceFactory : IBrowserSurfaceFactory
{
    private int created;

    /// <summary>Profile keys requested, in order — one entry per lane actually created.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>Every lane handed out, so a test can inspect what ran on which browser.</summary>
    public List<FakeLane> Lanes { get; } = [];

    /// <summary>
    /// Shapes each new surface's eval responses. The default answers the probes every run makes -
    /// the page-busy check and the resolver - so a lane behaves like a working browser unless a
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

    public Task<IBrowserLane> CreateLaneAsync(string profileKey, CancellationToken ct)
    {
        lock (Requested)
        {
            Requested.Add(profileKey);
            var lane = new FakeLane($"lane-{++created}", profileKey, new FakeBrowserSurface { DefaultEvalResponse = Responder });
            Lanes.Add(lane);
            return Task.FromResult<IBrowserLane>(lane);
        }
    }

    public sealed class FakeLane(string laneId, string profileKey, FakeBrowserSurface surface) : IBrowserLane
    {
        public string LaneId => laneId;
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

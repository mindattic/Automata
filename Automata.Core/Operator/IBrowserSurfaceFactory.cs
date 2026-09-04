namespace Automata.Core.Operator;

/// <summary>
/// One browser lane: an <see cref="IBrowserSurface"/> plus the identity of the profile backing it.
/// Disposing releases the underlying host.
/// </summary>
public interface IBrowserLane : IAsyncDisposable
{
    string LaneId { get; }

    /// <summary>Which named profile this lane's user-data folder belongs to. Two lanes with the
    /// same key share cookies and logins; different keys are fully isolated.</summary>
    string ProfileKey { get; }

    IBrowserSurface Surface { get; }
}

/// <summary>
/// Creates browser lanes. The seam that lets Automata.Core stay WebView2-free while the app and
/// the headless runner each host browsers their own way — and lets tests run the whole pool
/// against fakes.
/// </summary>
public interface IBrowserSurfaceFactory
{
    Task<IBrowserLane> CreateLaneAsync(string profileKey, CancellationToken ct);
}

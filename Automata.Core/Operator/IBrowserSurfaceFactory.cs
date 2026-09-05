namespace Automata.Core.Operator;

/// <summary>
/// One browser, held for as long as something is using it: an <see cref="IBrowserSurface"/> plus
/// the identity of the profile backing it. Disposing releases the underlying host.
/// <para>
/// A run holds exactly one of these at a time. Everything Automata does is sequential — a
/// collection walks its tasks in order, a loop walks its rows in order — so the browser is a
/// single place that each step leaves in the state the next one starts from. That is what makes a
/// task worth writing as a task rather than as a set of independent steps.
/// </para>
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>Which named profile this browser's user-data folder belongs to. Two sessions with
    /// the same key share cookies and logins; different keys are fully isolated.</summary>
    string ProfileKey { get; }

    IBrowserSurface Surface { get; }
}

/// <summary>
/// Creates browsers. The seam that lets Automata.Core stay WebView2-free while the app and the
/// headless runner each host a browser their own way — and lets tests run the whole engine against
/// fakes.
/// </summary>
public interface IBrowserSurfaceFactory
{
    Task<IBrowserSession> CreateAsync(string profileKey, CancellationToken ct);
}

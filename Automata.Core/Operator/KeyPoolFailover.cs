namespace Automata.Core.Operator;

/// <summary>
/// Shared "sticky failover" loop used by every <see cref="IToolCallingLlm"/> adapter that
/// supports more than one API key per provider: the first key is used until it fails with an
/// auth/rate-limit/server/network error, then the next key is tried. Each key still runs its
/// own retry budget (inside <c>callWithKey</c>) before being considered "failed" — this loop
/// only decides whether to move on to the NEXT key once a call has already given up.
/// </summary>
internal static class KeyPoolFailover
{
    /// <summary>
    /// Tries each key in <paramref name="keys"/> in order, returning the first successful
    /// result. Rethrows the last key's exception once every key has been tried.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="keys"/> is empty.</exception>
    public static async Task<T> ExecuteAsync<T>(
        IReadOnlyList<string> keys, CancellationToken ct, Func<string, Task<T>> callWithKey)
    {
        if (keys.Count == 0)
            throw new InvalidOperationException("No API key configured.");

        for (var i = 0; i < keys.Count; i++)
        {
            try
            {
                return await callWithKey(keys[i]);
            }
            // A caller-requested cancellation (e.g. the user stopping a run) is not "this key
            // failed" — trying the next key would silently keep the run going instead of
            // stopping it, so let it propagate instead of failing over.
            catch (Exception ex) when (i < keys.Count - 1 && !ct.IsCancellationRequested && IsKeyLevelFailure(ex))
            {
                // More keys remain and this one looks bad — fall through to try the next.
            }
        }
        // Unreachable: the `when` guard above is always false on the last key, so that
        // iteration always returns or rethrows. Kept for the compiler.
        throw new InvalidOperationException("Key pool exhausted with no successful call.");
    }

    /// <summary>
    /// True when a failure looks specific to the key that was used (bad/revoked credential,
    /// rate-limited, the provider is erroring, or a network failure) rather than a client-side
    /// bug that a different key wouldn't fix.
    /// </summary>
    public static bool IsKeyLevelFailure(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode is null
            || (int)hre.StatusCode is 401 or 408 or 429
            || (int)hre.StatusCode >= 500,
        TaskCanceledException => true,
        _ => false,
    };
}

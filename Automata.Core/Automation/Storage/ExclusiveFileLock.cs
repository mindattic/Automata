using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Automata.Core.Automation.Storage;

/// <summary>
/// Serialises a read-modify-write critical section, across threads AND across processes. Keyed by
/// a path-like string — a real file, or a name derived from the database path (see
/// <see cref="Data.AutomataDatabase"/>) for sections such as one dataset's appends.
/// <para>
/// Both halves are needed, for different reasons. <b>Threads:</b> a parallel for-each runs several
/// rows at once in one process, and every one of them may append to the same dataset. <b>Processes:</b>
/// the desktop app and the headless runner are separate executables over one workspace, and the
/// runner can be mid-run when someone opens the app — or two scheduled runs can overlap.
/// </para>
/// <para>
/// Without this, an append is a lost update waiting to happen: appending works out the union of
/// columns from what is already there and writes the result, so two writers racing lose whichever
/// update finished first. That was observed before this existed, by a parallel run that quietly
/// came back short.
/// </para>
/// <para>
/// The lock is a small sentinel file in the system temp folder, named from a hash of the key, so
/// nothing is ever left beside the user's data.
/// </para>
/// </summary>
public sealed class ExclusiveFileLock : IDisposable
{
    /// <summary>How long to keep trying before giving up and saying so.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcess =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim gate;
    private readonly FileStream handle;

    private ExclusiveFileLock(SemaphoreSlim gate, FileStream handle)
    {
        this.gate = gate;
        this.handle = handle;
    }

    /// <summary>
    /// Takes the lock for <paramref name="targetPath"/>, waiting up to <paramref name="timeout"/>.
    /// <para>
    /// The in-process gate is taken first and the cross-process handle second, always in that
    /// order — two locks acquired in inconsistent orders is how deadlocks are made.
    /// </para>
    /// </summary>
    public static ExclusiveFileLock Acquire(string targetPath, TimeSpan? timeout = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var key = Key(targetPath);
        var gate = InProcess.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        if (!gate.Wait(budget))
            throw new IOException($"Timed out waiting for another thread to finish writing '{targetPath}'.");

        try
        {
            return new ExclusiveFileLock(gate, OpenSentinel(key, targetPath, budget));
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    private static FileStream OpenSentinel(string key, string targetPath, TimeSpan budget)
    {
        var path = Path.Combine(Path.GetTempPath(), "MindAttic.Automata.Locks", key + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        while (true)
        {
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                // Someone else holds it. There is no wait-for-handle primitive here, so this polls
                // — briefly, because these critical sections are a file rewrite long, not a run long.
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Out of budget. Windows reports a share-mode denial either way round depending on
                // how the other holder opened it, so both land here rather than only the one.
                throw new IOException(
                    $"Timed out waiting for another process to finish writing '{targetPath}'. " +
                    "Another Automata run may still be working on it.", ex);
            }
        }
    }

    /// <summary>
    /// A stable, filename-safe key for a path. Hashed rather than sanitised so that two different
    /// paths can never flatten into one name and silently share a lock — and so the key does not
    /// leak the user's folder names into the temp directory.
    /// </summary>
    private static string Key(string targetPath)
    {
        var full = Path.GetFullPath(targetPath).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
    }

    public void Dispose()
    {
        // The gate is released whatever the handle does. A file handle that throws on close is
        // rare, but a gate never released is permanent: every later writer of that path in this
        // process waits its full budget and then reports a timeout that will never clear.
        try { handle.Dispose(); }
        finally { gate.Release(); }
    }
}

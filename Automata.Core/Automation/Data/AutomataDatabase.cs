using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Data;

/// <summary>
/// Where the database lives and how to open it: one SQLite file, by default
/// <c>%LocalAppData%\MindAttic\Automata\automata.db</c>.
/// <para>
/// <b>Overrides</b>, so tests, the UI harnesses and tools always run against a scratch database:
/// <c>AUTOMATA_DB_PATH</c> names the file outright; failing that, a harness that already points
/// <c>AUTOMATA_SETTINGS_PATH</c> at a scratch folder gets <c>automata.db</c> beside it — every
/// existing harness sets that variable, so none of them can touch the real database by accident.
/// </para>
/// <para>
/// Every context this hands out is on a migrated database: the first one brings the schema up to
/// date (EF migrations, under EF's own cross-process migration lock), so no code path can reach an
/// unmigrated file, whichever front door — the app, the runner or a test — opened it first.
/// </para>
/// </summary>
public sealed class AutomataDatabase : IDbContextFactory<AutomataDb>
{
    public const string FileName = "automata.db";
    public const string PathVariable = "AUTOMATA_DB_PATH";

    private readonly Lock gate = new();
    private readonly bool pooling;
    private volatile bool migrated;

    /// <summary>%LocalAppData%\MindAttic\Automata — machine-local, never synced.</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MindAttic", "Automata");

    public static string DefaultPath => Path.Combine(DefaultDataDirectory, FileName);

    /// <summary>The database file the environment asks for; see the type's remarks.</summary>
    public static string ResolvePath()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } explicitPath)
            return explicitPath;
        if (Environment.GetEnvironmentVariable("AUTOMATA_SETTINGS_PATH") is { Length: > 0 } settingsPath
            && Path.GetDirectoryName(Path.GetFullPath(settingsPath)) is { } scratch)
            return Path.Combine(scratch, FileName);
        return DefaultPath;
    }

    /// <param name="databasePath">The file; null resolves it from the environment.</param>
    /// <param name="pooling">Connection pooling. Tests turn it off so a scratch file can be deleted
    /// the moment they are done with it.</param>
    public AutomataDatabase(string? databasePath = null, bool pooling = true)
    {
        DatabasePath = Path.GetFullPath(databasePath ?? ResolvePath());
        this.pooling = pooling;
    }

    public string DatabasePath { get; }

    public string DataDirectory => Path.GetDirectoryName(DatabasePath)!;

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Pooling = pooling,
        // Seconds a statement waits on another connection's lock before failing. The app and the
        // headless runner share this file, and a scheduled run does not wait for the window.
        DefaultTimeout = 30,
    }.ToString();

    public AutomataDb CreateDbContext()
    {
        EnsureMigrated();
        return Open();
    }

    /// <summary>Creates the database or brings it to the current schema. Idempotent and cheap
    /// after the first call.</summary>
    public void EnsureMigrated()
    {
        if (migrated) return;
        lock (gate)
        {
            if (migrated) return;
            Directory.CreateDirectory(DataDirectory);
            using var db = Open();
            db.Database.Migrate();
            // WAL lets a reader (the sidebar listing runs) proceed while a writer (the runner
            // appending events) works. The mode is stored in the file, so this sticks.
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            migrated = true;
        }
    }

    /// <summary>A path-like key unique to this database, for <see cref="Storage.ExclusiveFileLock"/>
    /// critical sections that must hold across processes (dataset appends, the one-time import).</summary>
    internal string LockKey(string purpose) => DatabasePath + ".lock-" + purpose;

    private AutomataDb Open() =>
        new(new DbContextOptionsBuilder<AutomataDb>().UseSqlite(ConnectionString).Options);
}

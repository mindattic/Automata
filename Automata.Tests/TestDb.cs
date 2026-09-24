using Automata.Core.Automation.Data;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Microsoft.Data.Sqlite;

namespace Automata.Tests;

/// <summary>
/// A real SQLite database brought up by the app's own migrations — so tests exercise the schema
/// users actually get, not a model-only approximation.
/// <para>
/// A scratch FILE rather than <c>:memory:</c>, because the stores open a connection per operation
/// and several tests hammer one dataset from many threads at once, exactly as a parallel run does;
/// an in-memory database shared across connections would test a different locking model. The
/// migrations run once per test run into a template, and each test gets its own copy of it.
/// </para>
/// </summary>
internal sealed class TestDb : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "automata-tests", "db");

    private static readonly Lazy<string> Template = new(() =>
    {
        var dir = Path.Combine(Root, "template-" + Guid.NewGuid().ToString("n"));
        var path = Path.Combine(dir, AutomataDatabase.FileName);
        new AutomataDatabase(path, pooling: false).EnsureMigrated();
        SqliteConnection.ClearAllPools();
        return path;
    });

    private readonly string dir;

    public TestDb()
    {
        dir = Path.Combine(Root, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AutomataDatabase.FileName);
        File.Copy(Template.Value, path);
        Database = new AutomataDatabase(path, pooling: false);
    }

    /// <summary>A database created from nothing by running the migrations — for the test that
    /// proves they apply.</summary>
    public static TestDb Fresh()
    {
        var db = new TestDb();
        File.Delete(db.Database.DatabasePath);
        return db;
    }

    public AutomataDatabase Database { get; }

    public CollectionStore Collections() => new(Database);
    public DatasetStore Datasets() => new(Database);
    public RunStore Runs() => new(Database);
    public ScheduleStore Schedule() => new(Database);
    public ParkedRunStore Parked() => new(Database);
    public AutomataSettingsStore Settings() => new(Database);

    public ArchiveService Archive() =>
        new(Collections(), null, Datasets(), Runs(), Schedule(), Parked(), Settings());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* a handle still closing; the temp folder is scratch anyway */ }
        catch (UnauthorizedAccessException) { }
    }
}

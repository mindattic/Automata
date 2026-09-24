using Automata.Core.Automation.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>The database itself: the migrations build the schema users get, and the model the
/// code expects is the one the migrations describe.</summary>
[TestFixture]
public class DatabaseTests
{
    [Test]
    public void TheMigrationsCreateTheDatabaseFromNothing()
    {
        using var scratch = TestDb.Fresh();
        Assert.That(File.Exists(scratch.Database.DatabasePath), Is.False);

        scratch.Database.EnsureMigrated();

        using var db = scratch.Database.CreateDbContext();
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(scratch.Database.DatabasePath), Is.True);
            Assert.That(db.Database.GetAppliedMigrations().Single(), Does.EndWith("_Initial"));
            Assert.That(db.Database.GetPendingMigrations(), Is.Empty);
        });

        var tables = Tables(scratch.Database);
        Assert.That(tables, Is.SupersetOf(new[]
        {
            "Collections", "Tasks", "Datasets", "DatasetRows", "Runs", "RunEvents", "RunOutputs",
            "Schedule", "ParkedRuns", "Settings", "Meta", "__EFMigrationsHistory",
        }));
    }

    /// <summary>A model change without a migration would fail every user's startup; catch it here.</summary>
    [Test]
    public void TheModelHasNoChangesMissingAMigration()
    {
        using var scratch = new TestDb();
        using var db = scratch.Database.CreateDbContext();
        Assert.That(db.Database.HasPendingModelChanges(), Is.False,
            "run: dotnet ef migrations add <Name> --project Automata.Core --output-dir Automation/Data/Migrations");
    }

    [Test]
    public void MigratingTwiceIsHarmless()
    {
        using var scratch = new TestDb();
        scratch.Database.EnsureMigrated();
        new AutomataDatabase(scratch.Database.DatabasePath, pooling: false).EnsureMigrated();

        using var db = scratch.Database.CreateDbContext();
        Assert.That(db.Database.GetAppliedMigrations().Count(), Is.EqualTo(1));
    }

    /// <summary>The engine-settings complex types are columns on their rows, not tables of their own.</summary>
    [Test]
    public void ValueGroupsAreColumnsOnTheirRows()
    {
        using var scratch = new TestDb();
        var columns = Columns(scratch.Database, "Tasks");
        Assert.That(columns, Is.SupersetOf(new[]
        {
            "Settings_SelfHeal", "Settings_Retry_MaxAttempts", "Demo_Key", "Steps", "DeletedUtc",
        }));
    }

    [Test]
    public void ThePathComesFromTheEnvironmentWhenAsked()
    {
        var previousDb = Environment.GetEnvironmentVariable(AutomataDatabase.PathVariable);
        var previousSettings = Environment.GetEnvironmentVariable("AUTOMATA_SETTINGS_PATH");
        try
        {
            Environment.SetEnvironmentVariable(AutomataDatabase.PathVariable, null);
            Environment.SetEnvironmentVariable("AUTOMATA_SETTINGS_PATH", @"C:\scratch\harness\settings.json");
            Assert.That(AutomataDatabase.ResolvePath(), Is.EqualTo(@"C:\scratch\harness\automata.db"),
                "a harness that points settings at a scratch folder gets its database there too");

            Environment.SetEnvironmentVariable(AutomataDatabase.PathVariable, @"C:\elsewhere\x.db");
            Assert.That(AutomataDatabase.ResolvePath(), Is.EqualTo(@"C:\elsewhere\x.db"));

            Environment.SetEnvironmentVariable(AutomataDatabase.PathVariable, null);
            Environment.SetEnvironmentVariable("AUTOMATA_SETTINGS_PATH", null);
            Assert.That(AutomataDatabase.ResolvePath(), Does.EndWith(@"MindAttic\Automata\automata.db"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutomataDatabase.PathVariable, previousDb);
            Environment.SetEnvironmentVariable("AUTOMATA_SETTINGS_PATH", previousSettings);
        }
    }

    private static List<string> Tables(AutomataDatabase database) =>
        Query(database, "SELECT name FROM sqlite_master WHERE type = 'table'");

    private static List<string> Columns(AutomataDatabase database, string table) =>
        Query(database, $"SELECT name FROM pragma_table_info('{table}')");

    private static List<string> Query(AutomataDatabase database, string sql)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}

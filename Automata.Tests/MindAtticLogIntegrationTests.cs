using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MindAttic.Log;
using MindAttic.Log.Extensions;
using MindAttic.Log.Schema;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Proves the MindAttic.Log integration added to AddAutomataCore (see
/// Automata.Core/Extensions/ServiceCollectionExtensions.cs) actually writes into the same
/// automata.db file EF owns, alongside its own tables, through the ordinary ILogger&lt;T&gt;
/// call sites every store already uses — not just that the package reference compiles.
/// </summary>
public class MindAtticLogIntegrationTests
{
    [Test]
    public void AddMindAtticLog_Writes_Into_The_Same_File_As_Automata_Own_Tables()
    {
        using var db = TestDb.Fresh();
        db.Database.EnsureMigrated(); // same call App.xaml.cs makes before anything reads a store

        var services = new ServiceCollection();
        services.AddMindAtticLog(o =>
        {
            o.Application = "Automata";
            o.Destination = LogDestination.Sqlite;
            o.SqlitePath = db.Database.DatabasePath;
        });

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<MindAtticLogIntegrationTests>>();
        logger.LogWarning("Integration test wrote this at {Utc}", DateTime.UtcNow);

        // Force the batched sink to flush before asserting — disposing the provider disposes the
        // registered Serilog logger (AddSerilog(..., dispose: true)), which disposes the sink.
        provider.Dispose();
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var reader = tables.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));

            Assert.That(names, Does.Contain(LogSchema.TableName), "MindAttic_Log table should exist in automata.db.");
            Assert.That(names, Does.Contain("Collections"), "EF's own tables should be untouched by the log sink.");
        }

        using var rows = connection.CreateCommand();
        rows.CommandText = $"SELECT Application, Message FROM {LogSchema.TableName};";
        using var rowReader = rows.ExecuteReader();

        Assert.That(rowReader.Read(), Is.True, "Expected the logged warning to have reached the table.");
        Assert.That(rowReader.GetString(0), Is.EqualTo("Automata"));
        Assert.That(rowReader.GetString(1), Does.Contain("Integration test wrote this"));
    }
}

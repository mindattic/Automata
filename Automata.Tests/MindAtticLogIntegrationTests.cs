using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MindAttic.Log.Extensions;
using MindAttic.Log.Migration;
using MindAttic.Log.Schema;
using NUnit.Framework;

namespace Automata.Tests;

/// <summary>
/// Proves the MindAttic.Log integration added to AddAutomataCore (see
/// Automata.Core/Extensions/ServiceCollectionExtensions.cs) writes through the ordinary
/// ILogger&lt;T&gt; call sites every store already uses, into its own standalone database —
/// NOT into automata.db (Phase 2, MindAttic.Log 3.0.0; Phase 1 shared automata.db itself, which is
/// superseded — see MindAttic.Log's docs/BIBLE.md §1.1) — and that Phase 1's rows migrate across.
/// </summary>
public class MindAtticLogIntegrationTests
{
    [Test]
    public void AddMindAtticLog_Writes_Into_A_Standalone_Database_Not_Automata_Own_File()
    {
        using var db = TestDb.Fresh();
        db.Database.EnsureMigrated(); // same call App.xaml.cs makes before anything reads a store

        var standaloneDbPath = Path.Combine(Path.GetTempPath(), $"automata-standalone-log-{Guid.NewGuid()}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddMindAtticLog(o =>
            {
                o.Application = "Automata";
                o.DbPath = standaloneDbPath;
            });

            using var provider = services.BuildServiceProvider();
            var logger = provider.GetRequiredService<ILogger<MindAtticLogIntegrationTests>>();
            logger.LogWarning("Integration test wrote this at {Utc}", DateTime.UtcNow);

            // Force the batched sink to flush before asserting — disposing the provider disposes
            // the registered Serilog logger (AddSerilog(..., dispose: true)), which disposes the sink.
            provider.Dispose();
            SqliteConnection.ClearAllPools();

            using (var automataConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = db.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                automataConnection.Open();
                using var tables = automataConnection.CreateCommand();
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                using var reader = tables.ExecuteReader();
                var names = new List<string>();
                while (reader.Read()) names.Add(reader.GetString(0));

                Assert.That(names, Does.Not.Contain(LogSchema.TableName), "automata.db must no longer gain a MindAttic_Log table (Phase 2).");
                Assert.That(names, Does.Contain("Collections"), "EF's own tables should be untouched.");
            }

            using var standaloneConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = standaloneDbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            standaloneConnection.Open();
            using var rows = standaloneConnection.CreateCommand();
            rows.CommandText = $"SELECT Application, Message FROM {LogSchema.TableName};";
            using var rowReader = rows.ExecuteReader();

            Assert.That(rowReader.Read(), Is.True, "Expected the logged warning to have reached the standalone database.");
            Assert.That(rowReader.GetString(0), Is.EqualTo("Automata"));
            Assert.That(rowReader.GetString(1), Does.Contain("Integration test wrote this"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(standaloneDbPath)) File.Delete(standaloneDbPath);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(standaloneDbPath + suffix)) File.Delete(standaloneDbPath + suffix);
        }
    }

    [Test]
    public async Task MigrateFromSqliteAsync_Carries_Phase1_Rows_Out_Of_Automata_Db_Into_The_Standalone_Database()
    {
        using var db = TestDb.Fresh();
        db.Database.EnsureMigrated();

        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db.Database.DatabasePath }.ToString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = LogSchema.CreateTableSqlite;
            create.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = $"""
                INSERT INTO {LogSchema.TableName} (TimestampUtc, Level, Application, Message)
                VALUES ('2026-01-01T00:00:00.000Z', 2, 'Automata', 'Phase 1 row');
                """;
            insert.ExecuteNonQuery();
        }

        var standaloneDbPath = Path.Combine(Path.GetTempPath(), $"automata-standalone-log-{Guid.NewGuid()}.db");
        try
        {
            var copied = await MindAtticLogMigrator.MigrateFromSqliteAsync(
                db.Database.DatabasePath, standaloneDbPath, dropSource: true);

            Assert.That(copied, Is.EqualTo(1));

            SqliteConnection.ClearAllPools();
            using (var automataConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = db.Database.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                automataConnection.Open();
                using var tables = automataConnection.CreateCommand();
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                using var reader = tables.ExecuteReader();
                var names = new List<string>();
                while (reader.Read()) names.Add(reader.GetString(0));
                Assert.That(names, Does.Not.Contain(LogSchema.TableName), "dropSource: true should have removed Phase 1's table.");
            }

            using var standaloneConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = standaloneDbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            standaloneConnection.Open();
            using var rows = standaloneConnection.CreateCommand();
            rows.CommandText = $"SELECT Message FROM {LogSchema.TableName};";
            Assert.That(rows.ExecuteScalar(), Is.EqualTo("Phase 1 row"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(standaloneDbPath)) File.Delete(standaloneDbPath);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(standaloneDbPath + suffix)) File.Delete(standaloneDbPath + suffix);
        }
    }
}

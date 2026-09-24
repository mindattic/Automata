using System.Text.Json;
using Automata.Core.Automation.Execution;
using Automata.Core.Automation.Model;
using Automata.Core.Automation.Scheduling;
using Automata.Core.Automation.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Automata.Core.Automation.Data;

/// <summary>
/// The Automata database: one SQLite file holding every collection and task, the datasets tasks
/// read and write, the run history, the schedule, parked runs and the app settings. Schema changes
/// ship as EF migrations (<c>Automation/Data/Migrations</c>), so an upgrade keeps all of it.
/// <para>
/// The model classes are mapped directly — the stores hand out the same <see cref="Collection"/>,
/// <see cref="TaskDefinition"/>, <see cref="ScheduleEntry"/> … the rest of the app has always
/// used. A task's step tree is recursive and always read and written whole, so it is one JSON
/// column serialized with <see cref="AutomataJson"/>: exactly the shape a task file on disk had,
/// which is what keeps export, import and the old files interchangeable.
/// </para>
/// </summary>
public sealed class AutomataDb(DbContextOptions<AutomataDb> options) : DbContext(options)
{
    /// <summary>Shadow column on collections and tasks: set when "deleted", which only hides the
    /// row (HOUSE-LAW-2). Every ordinary query filters it out.</summary>
    public const string DeletedUtc = "DeletedUtc";

    /// <summary>Shadow key of the single settings row.</summary>
    public const string SettingsKey = "Id";

    /// <summary>Shadow column that keeps the schedule in the order it was written.</summary>
    public const string SortOrder = "SortOrder";

    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<TaskDefinition> Tasks => Set<TaskDefinition>();
    public DbSet<DatasetRecord> Datasets => Set<DatasetRecord>();
    public DbSet<DatasetRow> DatasetRows => Set<DatasetRow>();
    public DbSet<RunManifest> Runs => Set<RunManifest>();
    public DbSet<RunEventRecord> RunEvents => Set<RunEventRecord>();
    public DbSet<RunOutputRecord> RunOutputs => Set<RunOutputRecord>();
    public DbSet<ScheduleEntry> Schedule => Set<ScheduleEntry>();
    public DbSet<ParkedRun> ParkedRuns => Set<ParkedRun>();
    public DbSet<AutomataSettings> Settings => Set<AutomataSettings>();
    public DbSet<MetaEntry> Meta => Set<MetaEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite has no native DateTimeOffset — stored as a sortable long so ORDER BY and range
        // queries work in SQL instead of client-side.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        // decimal as TEXT keeps exact values (SQLite REAL would round).
        builder.Properties<decimal>().HaveConversion<string>();
        builder.Properties<decimal?>().HaveConversion<string>();

        // Enums as their names: readable in any SQLite browser, and reordering an enum can't
        // silently remap stored rows.
        foreach (var enumType in new[] { typeof(RunTargetKind), typeof(ScheduleTargetKind) })
        {
            builder.Properties(enumType).HaveConversion<string>();
            builder.Properties(typeof(Nullable<>).MakeGenericType(enumType)).HaveConversion<string>();
        }
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Collection>(c =>
        {
            c.ToTable("Collections");
            c.HasKey(x => x.Id);
            c.Property(x => x.Name).UseCollation("NOCASE");
            // TaskOrder is a list of ids: EF stores a primitive collection as a JSON array column.
            // Engine-settings overrides are a fixed value group with no identity of their own — a
            // complex type, stored as columns on the row (Settings_SelfHeal, Settings_Retry_…).
            c.ComplexProperty(x => x.Settings, EngineOverride);
            c.Property<DateTimeOffset?>(DeletedUtc);
            c.HasQueryFilter(x => EF.Property<DateTimeOffset?>(x, DeletedUtc) == null);
        });

        model.Entity<TaskDefinition>(t =>
        {
            t.ToTable("Tasks");
            t.HasKey(x => x.Id);
            t.HasIndex(x => x.CollectionId);
            t.Property(x => x.Name).UseCollation("NOCASE");
            t.HasOne<Collection>().WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            // The step tree is recursive and always read and written whole: one JSON document in
            // exactly the shape a task file had. Inputs and outputs travel the same way.
            t.Property(x => x.Steps).HasAutomataJson(() => []);
            t.Property(x => x.Inputs).HasAutomataJson(() => []);
            t.Property(x => x.Outputs).HasAutomataJson(() => []);
            t.ComplexProperty(x => x.Settings, EngineOverride);
            t.ComplexProperty(x => x.Demo);
            t.Property<DateTimeOffset?>(DeletedUtc);
            t.HasQueryFilter(x => EF.Property<DateTimeOffset?>(x, DeletedUtc) == null);
        });

        model.Entity<DatasetRecord>(d =>
        {
            d.ToTable("Datasets");
            d.Property(x => x.Name).UseCollation("NOCASE");
            d.HasIndex(x => x.Name).IsUnique();
            d.HasMany(x => x.Rows).WithOne().HasForeignKey(x => x.DatasetId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DatasetRow>(r =>
        {
            r.ToTable("DatasetRows");
            r.HasIndex(x => new { x.DatasetId, x.Ordinal });
        });

        model.Entity<RunManifest>(r =>
        {
            r.ToTable("Runs");
            r.HasKey(x => x.RunId);
            r.HasIndex(x => x.StartedUtc);
            r.HasMany<RunEventRecord>().WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            r.HasMany<RunOutputRecord>().WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<RunEventRecord>(e =>
        {
            e.ToTable("RunEvents");
            e.HasIndex(x => new { x.RunId, x.TaskId });
        });
        model.Entity<RunOutputRecord>(o =>
        {
            o.ToTable("RunOutputs");
            o.HasIndex(x => new { x.RunId, x.TaskId });
        });

        model.Entity<ScheduleEntry>(s =>
        {
            s.ToTable("Schedule");
            s.HasKey(x => x.Id);
            s.Property(x => x.Triggers).HasAutomataJson(() => []);
            s.Property<int>(SortOrder);
        });

        model.Entity<ParkedRun>(p =>
        {
            p.ToTable("ParkedRuns");
            p.HasKey(x => x.RunId);
            p.Ignore(x => x.ResumeAtUtc);
            // A checkpoint is the walk's state — outputs, variables, a resume path — always
            // written and read whole, so it is one JSON document.
            p.Property(x => x.Checkpoint).HasAutomataJson(
                () => new ParkCheckpoint(default, "", [], "", "", [], new Dictionary<string, string>(), 0, 0));
        });

        model.Entity<AutomataSettings>(s =>
        {
            s.ToTable("Settings");
            s.Property<int>(SettingsKey).ValueGeneratedNever();
            s.HasKey(SettingsKey);
            s.ComplexProperty(x => x.EngineDefaults, EngineOverride);
        });

        model.Entity<MetaEntry>(m =>
        {
            m.ToTable("Meta");
            m.HasKey(x => x.Key);
        });
    }

    /// <summary>
    /// An engine-settings override is optional and EVERY property in it is optional, so a row of
    /// all-null columns could mean "no override" or "an override of nothing". The shadow
    /// discriminator column records which — though the stores never persist an empty override
    /// (SchemaMigration prunes it to null), so in practice it reads as "has an override".
    /// </summary>
    private static void EngineOverride(ComplexPropertyBuilder<EngineSettingsOverride> settings)
    {
        settings.HasDiscriminator();
        settings.ComplexProperty(x => x.Retry);
    }
}

/// <summary>A named dataset. Its rows live in <see cref="DatasetRow"/>, in order.</summary>
public sealed class DatasetRecord
{
    public int Id { get; set; }

    /// <summary>What tasks call it, e.g. "skus.csv". The extension picks the format: ".json" is a
    /// JSON array of objects, anything else is CSV.</summary>
    public string Name { get; set; } = "";

    /// <summary>The CSV header, in order. Empty for a JSON dataset, whose columns are whatever its
    /// rows hold.</summary>
    public List<string> Columns { get; set; } = [];

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }

    public List<DatasetRow> Rows { get; set; } = [];
}

/// <summary>One row of a dataset, as a JSON object: string values for a CSV row, the original
/// object (nested values and all) for a JSON one.</summary>
public sealed class DatasetRow
{
    public long Id { get; set; }
    public int DatasetId { get; set; }
    public int Ordinal { get; set; }
    public string Json { get; set; } = "{}";
}

/// <summary>One event a task reported during a run, in the order it was reported.</summary>
public sealed class RunEventRecord
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }

    /// <summary>The event, serialized on one line — what a line of events.jsonl used to be.</summary>
    public string Json { get; set; } = "{}";
}

/// <summary>One value a step published during a run.</summary>
public sealed class RunOutputRecord
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Bookkeeping the database keeps about itself, e.g. that the one-time import of the
/// old JSON files has happened.</summary>
public sealed class MetaEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

internal static class JsonColumnExtensions
{
    /// <summary>
    /// Stores a property as one JSON text column, serialized with Automata's own options (compact)
    /// so a column holds exactly what the model's JSON files hold. The comparer makes EF detect
    /// in-place edits of a tracked instance.
    /// </summary>
    public static PropertyBuilder<T> HasAutomataJson<T>(this PropertyBuilder<T> property, Func<T> empty)
        where T : class
    {
        property.HasConversion(
            new ValueConverter<T, string>(
                v => JsonSerializer.Serialize(v, AutomataJson.Compact),
                s => Deserialize(s, empty)),
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, AutomataJson.Compact) == JsonSerializer.Serialize(b, AutomataJson.Compact),
                v => JsonSerializer.Serialize(v, AutomataJson.Compact).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, AutomataJson.Compact), AutomataJson.Compact)!));
        return property;
    }

    private static T Deserialize<T>(string text, Func<T> empty) where T : class =>
        string.IsNullOrWhiteSpace(text)
            ? empty()
            : JsonSerializer.Deserialize<T>(text, AutomataJson.Compact) ?? empty();
}

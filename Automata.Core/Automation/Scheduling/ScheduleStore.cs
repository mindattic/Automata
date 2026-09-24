using Automata.Core.Automation.Data;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Scheduling;

/// <summary>
/// The schedule, in the <c>Schedule</c> table — one row per entry, its triggers a JSON column,
/// kept in the order it was written.
/// <para>
/// Whole-list read and write on purpose: entries are few, small and machine-maintained, and the
/// scheduler's tick reads them all, updates their bookkeeping and writes them all back.
/// </para>
/// </summary>
public sealed class ScheduleStore
{
    private readonly Lock gate = new();

    public ScheduleStore(AutomataDatabase database) => Database = database;

    public AutomataDatabase Database { get; }

    public List<ScheduleEntry> Load()
    {
        using var db = Database.CreateDbContext();
        return db.Schedule.AsNoTracking()
            .OrderBy(e => EF.Property<int>(e, AutomataDb.SortOrder))
            .ToList();
    }

    /// <summary>Replaces the whole schedule with <paramref name="entries"/>, in that order.</summary>
    public void Save(IEnumerable<ScheduleEntry> entries)
    {
        var list = entries.ToList();
        lock (gate)
        {
            using var db = Database.CreateDbContext();
            using var tx = db.Database.BeginTransaction();
            var keep = list.Select(e => e.Id).ToList();
            db.Schedule.Where(e => !keep.Contains(e.Id)).ExecuteDelete();
            var existing = db.Schedule.AsNoTracking().Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                var entry = db.Entry(list[i]);
                entry.State = existing.Contains(list[i].Id) ? EntityState.Modified : EntityState.Added;
                entry.Property(AutomataDb.SortOrder).CurrentValue = i;
                existing.Add(list[i].Id);
            }
            db.SaveChanges();
            tx.Commit();
        }
    }

    public ScheduleEntry? Get(string id)
    {
        using var db = Database.CreateDbContext();
        return db.Schedule.AsNoTracking().FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Adds (at the end) or replaces an entry by id.</summary>
    public void Upsert(ScheduleEntry entry)
    {
        lock (gate)
        {
            using var db = Database.CreateDbContext();
            var current = db.Schedule.AsNoTracking()
                .Where(e => e.Id == entry.Id)
                .Select(e => (int?)EF.Property<int>(e, AutomataDb.SortOrder))
                .FirstOrDefault();
            var tracked = db.Entry(entry);
            if (current is { } order)
            {
                tracked.State = EntityState.Modified;
                tracked.Property(AutomataDb.SortOrder).CurrentValue = order;
            }
            else
            {
                var last = db.Schedule.Max(e => (int?)EF.Property<int>(e, AutomataDb.SortOrder)) ?? -1;
                tracked.State = EntityState.Added;
                tracked.Property(AutomataDb.SortOrder).CurrentValue = last + 1;
            }
            db.SaveChanges();
        }
    }

    public bool Remove(string id)
    {
        using var db = Database.CreateDbContext();
        return db.Schedule.Where(e => e.Id == id).ExecuteDelete() > 0;
    }
}

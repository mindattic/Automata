using Automata.Core.Automation.Data;
using Microsoft.EntityFrameworkCore;

namespace Automata.Core.Automation.Execution;

/// <summary>
/// Runs waiting out a long pause, one row each in the <c>ParkedRuns</c> table.
/// <para>
/// Parked runs appear and disappear independently and often at the same time — the runner may
/// resume one while a browser run parks another — so each is its own row, and a resumed run leaves
/// no residue at all: the row is removed, not marked. (This is machine state rather than anybody's
/// work, which is why removal here is real rather than a soft delete.)
/// </para>
/// </summary>
public sealed class ParkedRunStore
{
    public ParkedRunStore(AutomataDatabase database) => Database = database;

    public AutomataDatabase Database { get; }

    public void Save(ParkedRun parked)
    {
        using var db = Database.CreateDbContext();
        var exists = db.ParkedRuns.Any(p => p.RunId == parked.RunId);
        db.Entry(parked).State = exists ? EntityState.Modified : EntityState.Added;
        db.SaveChanges();
    }

    public ParkedRun? Get(string runId)
    {
        using var db = Database.CreateDbContext();
        return db.ParkedRuns.AsNoTracking().FirstOrDefault(p => p.RunId == runId);
    }

    /// <summary>Everything parked, soonest to resume first.</summary>
    public IReadOnlyList<ParkedRun> List()
    {
        using var db = Database.CreateDbContext();
        return db.ParkedRuns.AsNoTracking().ToList()
            .OrderBy(p => p.ResumeAtUtc)
            .ToList();
    }

    /// <summary>Parked runs whose wait is over.</summary>
    public IReadOnlyList<ParkedRun> Due(DateTimeOffset now) =>
        List().Where(p => p.ResumeAtUtc <= now).ToList();

    public bool Remove(string runId)
    {
        using var db = Database.CreateDbContext();
        return db.ParkedRuns.Where(p => p.RunId == runId).ExecuteDelete() > 0;
    }
}

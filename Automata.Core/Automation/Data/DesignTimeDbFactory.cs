using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Automata.Core.Automation.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without the app's host.</summary>
internal sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<AutomataDb>
{
    public AutomataDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AutomataDb>().UseSqlite("Data Source=design-time.db").Options);
}

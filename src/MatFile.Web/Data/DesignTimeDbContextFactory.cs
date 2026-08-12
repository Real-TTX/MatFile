using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MatFile.Web.Data;

/// <summary>Wird nur von "dotnet ef" beim Erzeugen von Migrationen verwendet.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MatFileDbContext>
{
    public MatFileDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MatFileDbContext>()
            .UseSqlite("Data Source=matfile-design.db")
            .Options;

        return new MatFileDbContext(options);
    }
}

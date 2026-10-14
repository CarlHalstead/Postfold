using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Postfold.Storage.Contexts;

internal sealed class ProfileDbContextFactory : IDesignTimeDbContextFactory<ProfileDbContext>
{
    public ProfileDbContext CreateDbContext(string[] args)
    {
        // Design-time commands cannot discover or open a normal desktop profile.
        DbContextOptions<ProfileDbContext> options = new DbContextOptionsBuilder<ProfileDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = ":memory:",
                ForeignKeys = true,
                Pooling = false
            }.ToString())
            .Options;
        return new ProfileDbContext(options);
    }
}

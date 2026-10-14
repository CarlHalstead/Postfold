using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Postfold.Storage.Configurations;
using Postfold.Storage.Models;

namespace Postfold.Engine.Tests.Fixtures.Storage;

internal sealed class EvolutionContext(DbContextOptions<EvolutionContext> options) : DbContext(options)
{
    internal static ProfileMigrationPlan CreatePlan()
    {
        return ProfileMigrationPlan.Create(Create);
    }

    internal static DbContext Create(SqliteConnection connection)
    {
        return new EvolutionContext(new DbContextOptionsBuilder<EvolutionContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(EvolutionContext).Assembly.FullName))
            .AddInterceptors(new MigrationBarrierInterceptor(connection.DataSource != ":memory:"))
            .Options);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ProfileConfiguration.Configure(modelBuilder);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Postfold.Storage.Configurations;

namespace Postfold.Storage.Contexts;

internal sealed class ProfileDbContext(DbContextOptions<ProfileDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Retained SQL has reviewed indexes; do not add convention indexes to every foreign key.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ProfileConfiguration.Configure(modelBuilder);
    }
}

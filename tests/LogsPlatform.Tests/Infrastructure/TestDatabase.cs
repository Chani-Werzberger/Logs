using LogsPlatform.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Infrastructure;

public static class TestDatabase
{
    public const string ConnectionString =
        "Server=(localdb)\\mssqllocaldb;Database=LogsPlatformTests;Trusted_Connection=True;";

    public static LogsPlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        var context = new LogsPlatformDbContext(options);
        context.Database.EnsureDeleted();
        context.Database.Migrate();
        return context;
    }

    // For repositories converted to IDbContextFactory<LogsPlatformDbContext> (see Program.cs).
    // Does not EnsureDeleted/Migrate — call CreateContext() once first to establish schema.
    public static IDbContextFactory<LogsPlatformDbContext> CreateFactory() => new TestDbContextFactory();

    private sealed class TestDbContextFactory : IDbContextFactory<LogsPlatformDbContext>
    {
        public LogsPlatformDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<LogsPlatformDbContext>()
                .UseSqlServer(ConnectionString)
                .Options;
            return new LogsPlatformDbContext(options);
        }
    }
}

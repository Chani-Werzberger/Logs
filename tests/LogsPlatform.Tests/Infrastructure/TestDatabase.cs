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
}

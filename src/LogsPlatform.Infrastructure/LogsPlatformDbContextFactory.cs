using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LogsPlatform.Infrastructure;

// Used only by the `dotnet ef` CLI to construct the DbContext for migrations,
// independent of LogsPlatform.Web's runtime DI setup.
public class LogsPlatformDbContextFactory : IDesignTimeDbContextFactory<LogsPlatformDbContext>
{
    public LogsPlatformDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LogsPlatformDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost\\SQLEXPRESS;Database=LogsPlatformDev;Trusted_Connection=True;TrustServerCertificate=True;");
        return new LogsPlatformDbContext(optionsBuilder.Options);
    }
}

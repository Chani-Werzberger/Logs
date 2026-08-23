using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Tests.Infrastructure;
using LogsPlatform.Web.Services.Analysis;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Tests.Web;

[Collection("Database")]
public class NewExceptionDetectorTests
{
    private static async Task<(int ApplicationId, int EnvironmentId)> SeedAppEnvAsync(LogsPlatformDbContext context, string appName)
    {
        var app = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(app);
        await context.SaveChangesAsync();
        var env = new AppEnvironment { ApplicationId = app.Id, Name = "Production", IsProduction = true };
        context.AppEnvironments.Add(env);
        await context.SaveChangesAsync();
        return (app.Id, env.Id);
    }

    [Fact]
    public async Task RunAsync_RecentlyFirstSeenGroup_CreatesNewExceptionFinding()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-new", ExceptionType = "System.TimeoutException",
            MessageTemplate = "timed out", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-1), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 1
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        context.Events.Add(new Event
        {
            ApplicationId = appId, EnvironmentId = envId, Timestamp = DateTime.UtcNow,
            Severity = 17, Message = "boom", ExceptionGroupId = group.Id
        });
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new NewExceptionDetector(context, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var finding = await verifyContext.Findings.FirstOrDefaultAsync(f => f.ApplicationId == appId && f.Type == FindingType.NewException);

        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
        Assert.Equal(ConfidenceLevel.High, finding.ConfidenceLevel);
    }

    [Fact]
    public async Task RunAsync_OldExistingGroup_NoFindingCreated()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId) = await SeedAppEnvAsync(context, "NewExceptionDetectorOldGroupTestApp");

        var group = new ExceptionGroup
        {
            ApplicationId = appId, Fingerprint = "fp-old", ExceptionType = "System.Exception",
            MessageTemplate = "old", RepresentativeStackTrace = "at Foo.Bar()",
            FirstSeenAt = DateTime.UtcNow.AddDays(-10), LastSeenAt = DateTime.UtcNow, OccurrenceCount = 5
        };
        context.ExceptionGroups.Add(group);
        await context.SaveChangesAsync();

        var findingRepository = new FindingRepository(context);
        var writer = new FindingWriter(findingRepository);
        var detector = new NewExceptionDetector(context, writer);

        await detector.RunAsync(appId, envId);

        var options = new DbContextOptionsBuilder<LogsPlatformDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options;
        await using var verifyContext = new LogsPlatformDbContext(options);
        var findingCount = await verifyContext.Findings.CountAsync(f => f.ApplicationId == appId);

        Assert.Equal(0, findingCount);
    }
}

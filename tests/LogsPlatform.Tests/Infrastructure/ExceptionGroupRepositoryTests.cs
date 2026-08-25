using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ExceptionGroupRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task GetOrCreateAsync_NewFingerprint_CreatesGroup()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupCreateTestApp");
        var repository = new ExceptionGroupRepository(context);
        var seenAt = DateTime.UtcNow;

        var group = await repository.GetOrCreateAsync(appId, "fp-1", "System.Exception", "boom", "at Foo()", seenAt);

        Assert.Equal("fp-1", group.Fingerprint);
        Assert.Equal(1, group.OccurrenceCount);
        Assert.Equal(seenAt, group.FirstSeenAt);
    }

    [Fact]
    public async Task GetOrCreateAsync_ExistingFingerprint_IncrementsCountAndUpdatesLastSeenAt()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupReuseTestApp");
        var repository = new ExceptionGroupRepository(context);
        var firstSeenAt = DateTime.UtcNow;
        var secondSeenAt = firstSeenAt.AddMinutes(5);
        var first = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", firstSeenAt);

        var second = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", secondSeenAt);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.OccurrenceCount);
        Assert.Equal(secondSeenAt, second.LastSeenAt);
        Assert.Equal(firstSeenAt, second.FirstSeenAt);
    }

    [Fact]
    public async Task GetOrCreateAsync_ThirdOccurrenceOfSameFingerprint_CountReachesThree()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupTripleOccurrenceTestApp");
        var repository = new ExceptionGroupRepository(context);

        await repository.GetOrCreateAsync(appId, "fp-triple", "System.Exception", "boom", "at Foo()", DateTime.UtcNow);
        await repository.GetOrCreateAsync(appId, "fp-triple", "System.Exception", "boom", "at Foo()", DateTime.UtcNow);
        var third = await repository.GetOrCreateAsync(appId, "fp-triple", "System.Exception", "boom", "at Foo()", DateTime.UtcNow);

        Assert.Equal(3, third.OccurrenceCount);
    }

    [Fact]
    public async Task GetOrCreateAsync_FingerprintAlreadyPersistedByBypassedInsert_ReturnsExistingGroupWithoutThrowing()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupUniqueViolationRetryTestApp");
        var repository = new ExceptionGroupRepository(context);
        var seenAt = DateTime.UtcNow;

        // Simulate a concurrent request winning the (ApplicationId, Fingerprint) race: insert the
        // row directly against the DB, bypassing the repository's own existence check, right
        // before calling GetOrCreateAsync with the same fingerprint.
        context.ExceptionGroups.Add(new ExceptionGroup
        {
            ApplicationId = appId,
            Fingerprint = "fp-race",
            ExceptionType = "System.Exception",
            MessageTemplate = "boom",
            RepresentativeStackTrace = "at Foo()",
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            OccurrenceCount = 1
        });
        await context.SaveChangesAsync();

        var result = await repository.GetOrCreateAsync(appId, "fp-race", "System.Exception", "boom", "at Foo()", seenAt.AddMinutes(1));

        Assert.Equal("fp-race", result.Fingerprint);
        Assert.Equal(1, await context.ExceptionGroups.CountAsync(g => g.Fingerprint == "fp-race"));
        // The recovered "winner" row's own occurrence (created via the bypassed insert, count=1)
        // plus this call's occurrence (the request that lost the insert race) are both real,
        // distinct exception occurrences - the count reflects both.
        Assert.Equal(2, result.OccurrenceCount);
    }
}

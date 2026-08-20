using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
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
    public async Task GetOrCreateAsync_ExistingFingerprint_ReturnsSameGroupWithoutIncrementingCount()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "ExceptionGroupReuseTestApp");
        var repository = new ExceptionGroupRepository(context);
        var first = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", DateTime.UtcNow);

        var second = await repository.GetOrCreateAsync(appId, "fp-2", "System.Exception", "boom", "at Foo()", DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, second.OccurrenceCount);
    }
}

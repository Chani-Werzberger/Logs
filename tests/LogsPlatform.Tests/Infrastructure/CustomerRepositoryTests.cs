using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class CustomerRepositoryTests
{
    private static async Task<int> CreateTestApplicationAsync(LogsPlatformDbContext context, string name)
    {
        var application = new Application { Name = name, CreatedAt = DateTime.UtcNow };
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return application.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsCustomer_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerAddTestApp");
        var repository = new CustomerRepository(TestDatabase.CreateFactory());

        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "Acme Corp" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("cust-1", loaded!.ExternalCustomerId);
        Assert.Equal("Acme Corp", loaded.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerFilterTestApp");
        var repository = new CustomerRepository(TestDatabase.CreateFactory());

        var active = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-active", Name = "Active" });
        var toDeactivate = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-inactive", Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesName_LeavesExternalCustomerIdUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerRenameTestApp");
        var repository = new CustomerRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("cust-1", renamed.ExternalCustomerId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerDeactivateTestApp");
        var repository = new CustomerRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-1", Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateExternalIdFailure_SubsequentUniqueExternalIdStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var appId = await CreateTestApplicationAsync(context, "CustomerCircuitTestApp");
        var repository = new CustomerRepository(TestDatabase.CreateFactory());

        await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-dup", Name = "First" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-dup", Name = "Second" }));

        var created = await repository.AddAsync(new Customer { ApplicationId = appId, ExternalCustomerId = "cust-unique", Name = "Third" });

        Assert.Equal("cust-unique", created.ExternalCustomerId);
    }
}

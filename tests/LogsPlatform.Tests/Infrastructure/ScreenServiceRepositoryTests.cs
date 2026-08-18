// tests/LogsPlatform.Tests/Infrastructure/ScreenServiceRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ScreenServiceRepositoryTests
{
    private static async Task<int> CreateTestModuleAsync(LogsPlatformDbContext context, string appName, string moduleName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return module.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsScreenService_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceAddTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        var created = await repository.AddAsync(new ScreenService
        {
            ModuleId = moduleId,
            Name = "PaymentGateway",
            Type = ScreenServiceType.Service
        });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("PaymentGateway", loaded!.Name);
        Assert.Equal(ScreenServiceType.Service, loaded.Type);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByModuleIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceFilterTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        var active = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "Active", Type = ScreenServiceType.Screen });
        var toDeactivate = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "WillBeInactive", Type = ScreenServiceType.Screen });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByModuleIdAsync(moduleId);
        var withInactive = await repository.GetByModuleIdAsync(moduleId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceRenameTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);
        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "OldName", Type = ScreenServiceType.Screen });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceDeactivateTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);
        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "ToDeactivate", Type = ScreenServiceType.Screen });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var moduleId = await CreateTestModuleAsync(context, "ScreenServiceCircuitTestApp", "Payments");
        var repository = new ScreenServiceRepository(context);

        await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "DupService", Type = ScreenServiceType.Service });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "DupService", Type = ScreenServiceType.Service }));

        var created = await repository.AddAsync(new ScreenService { ModuleId = moduleId, Name = "UniqueService", Type = ScreenServiceType.Service });

        Assert.Equal("UniqueService", created.Name);
    }
}

using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class ProcessNodeRepositoryTests
{
    private static async Task<int> CreateTestScreenServiceAsync(LogsPlatformDbContext context, string appName, string moduleName, string screenServiceName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        var screenService = new ScreenService { Name = screenServiceName, Type = ScreenServiceType.Service };
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return screenService.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsProcessNode_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessAddTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ChargeCard" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("ChargeCard", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByScreenServiceIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessFilterTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        var active = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByScreenServiceIdAsync(screenServiceId);
        var withInactive = await repository.GetByScreenServiceIdAsync(screenServiceId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessRenameTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessDeactivateTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessCircuitTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);

        await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "DupProcess" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "DupProcess" }));

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "UniqueProcess" });

        Assert.Equal("UniqueProcess", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var screenServiceId = await CreateTestScreenServiceAsync(context, "ProcessRenameConflictTestApp", "Payments", "PaymentGateway");
        var repository = new ProcessNodeRepository(context);
        await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "Taken" });
        var toRename = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new ProcessNode { ScreenServiceId = screenServiceId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}

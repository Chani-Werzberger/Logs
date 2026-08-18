using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class OperationRepositoryTests
{
    private static async Task<int> CreateTestProcessNodeAsync(LogsPlatformDbContext context, string appName, string moduleName, string screenServiceName, string processName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var module = new AppModule { Name = moduleName };
        var screenService = new ScreenService { Name = screenServiceName, Type = ScreenServiceType.Service };
        var process = new ProcessNode { Name = processName };
        screenService.Processes.Add(process);
        module.ScreenServices.Add(screenService);
        application.Modules.Add(module);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return process.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsOperation_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationAddTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "AuthorizePayment" });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal("AuthorizePayment", loaded!.Name);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByProcessIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationFilterTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        var active = await repository.AddAsync(new Operation { ProcessId = processId, Name = "Active" });
        var toDeactivate = await repository.AddAsync(new Operation { ProcessId = processId, Name = "WillBeInactive" });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByProcessIdAsync(processId);
        var withInactive = await repository.GetByProcessIdAsync(processId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNameAndDescription()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationRenameTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "OldName" });

        var renamed = await repository.RenameAsync(created.Id, "NewName", "new description");

        Assert.Equal("NewName", renamed.Name);
        Assert.Equal("new description", renamed.Description);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationDeactivateTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "ToDeactivate" });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_AfterDuplicateNameFailure_SubsequentUniqueNameStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationCircuitTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);

        await repository.AddAsync(new Operation { ProcessId = processId, Name = "DupOperation" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.AddAsync(new Operation { ProcessId = processId, Name = "DupOperation" }));

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "UniqueOperation" });

        Assert.Equal("UniqueOperation", created.Name);
    }

    [Fact]
    public async Task RenameAsync_ToExistingSiblingName_ThrowsAndSubsequentWriteStillSucceeds()
    {
        using var context = TestDatabase.CreateContext();
        var processId = await CreateTestProcessNodeAsync(context, "OperationRenameConflictTestApp", "Payments", "PaymentGateway", "ChargeCard");
        var repository = new OperationRepository(context);
        await repository.AddAsync(new Operation { ProcessId = processId, Name = "Taken" });
        var toRename = await repository.AddAsync(new Operation { ProcessId = processId, Name = "ToRename" });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RenameAsync(toRename.Id, "Taken", null));

        var created = await repository.AddAsync(new Operation { ProcessId = processId, Name = "StillWorks" });
        Assert.Equal("StillWorks", created.Name);
    }
}

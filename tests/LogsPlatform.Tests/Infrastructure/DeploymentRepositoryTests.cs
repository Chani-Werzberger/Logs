// tests/LogsPlatform.Tests/Infrastructure/DeploymentRepositoryTests.cs
using LogsPlatform.Domain.Entities;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using Xunit;

namespace LogsPlatform.Tests.Infrastructure;

[Collection("Database")]
public class DeploymentRepositoryTests
{
    private static async Task<(int ApplicationId, int EnvironmentId, int VersionId)> CreateTestFixtureAsync(LogsPlatformDbContext context, string appName)
    {
        var application = new Application { Name = appName, CreatedAt = DateTime.UtcNow };
        var environment = new AppEnvironment { Name = "Production", IsProduction = true };
        application.Environments.Add(environment);
        var version = new AppVersion { VersionNumber = "1.0.0", CreatedAt = DateTime.UtcNow };
        application.Versions.Add(version);
        context.Applications.Add(application);
        await context.SaveChangesAsync();
        return (application.Id, environment.Id, version.Id);
    }

    [Fact]
    public async Task AddAsync_PersistsDeployment_RetrievableByGetByIdAsync()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentAddTestApp");
        var repository = new DeploymentRepository(TestDatabase.CreateFactory());

        var created = await repository.AddAsync(new Deployment
        {
            ApplicationId = appId,
            EnvironmentId = envId,
            VersionId = versionId,
            DeployedAt = DateTime.UtcNow,
            Notes = "Initial deploy"
        });
        var loaded = await repository.GetByIdAsync(created.Id);

        Assert.NotNull(loaded);
        Assert.Equal(envId, loaded!.EnvironmentId);
        Assert.Equal(versionId, loaded.VersionId);
        Assert.Equal("Initial deploy", loaded.Notes);
        Assert.True(loaded.IsActive);
    }

    [Fact]
    public async Task GetByApplicationIdAsync_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentFilterTestApp");
        var repository = new DeploymentRepository(TestDatabase.CreateFactory());

        var active = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        var toDeactivate = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        await repository.DeactivateAsync(toDeactivate.Id);

        var defaultResult = await repository.GetByApplicationIdAsync(appId);
        var withInactive = await repository.GetByApplicationIdAsync(appId, includeInactive: true);

        Assert.Single(defaultResult);
        Assert.Equal(active.Id, defaultResult[0].Id);
        Assert.Equal(2, withInactive.Count);
    }

    [Fact]
    public async Task RenameAsync_UpdatesNotes_LeavesOtherFieldsUnchanged()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentRenameTestApp");
        var repository = new DeploymentRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow, Notes = "OldNotes" });

        var renamed = await repository.RenameAsync(created.Id, "NewNotes");

        Assert.Equal("NewNotes", renamed.Notes);
        Assert.Equal(envId, renamed.EnvironmentId);
        Assert.Equal(versionId, renamed.VersionId);
    }

    [Fact]
    public async Task DeactivateAsync_SetsIsActiveFalse()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentDeactivateTestApp");
        var repository = new DeploymentRepository(TestDatabase.CreateFactory());
        var created = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });

        await repository.DeactivateAsync(created.Id);

        var reloaded = await repository.GetByIdAsync(created.Id);
        Assert.False(reloaded!.IsActive);
    }

    [Fact]
    public async Task AddAsync_SameEnvironmentAndVersionTwice_BothSucceed()
    {
        using var context = TestDatabase.CreateContext();
        var (appId, envId, versionId) = await CreateTestFixtureAsync(context, "DeploymentRedeployTestApp");
        var repository = new DeploymentRepository(TestDatabase.CreateFactory());

        var first = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });
        var second = await repository.AddAsync(new Deployment { ApplicationId = appId, EnvironmentId = envId, VersionId = versionId, DeployedAt = DateTime.UtcNow });

        Assert.NotEqual(first.Id, second.Id);
    }
}

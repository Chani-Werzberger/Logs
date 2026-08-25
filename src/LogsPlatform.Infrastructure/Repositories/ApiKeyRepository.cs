using System.Security.Cryptography;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private const string KeyPrefix = "lgp_";

    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public ApiKeyRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<ApiKey?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ApiKeys.FindAsync(id);
    }

    public async Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.ApiKeys.AsNoTracking().Where(k => k.ApplicationId == applicationId);
        if (!includeRevoked)
        {
            query = query.Where(k => k.RevokedAt == null);
        }
        return await query.OrderBy(k => k.CreatedAt).ThenBy(k => k.Id).ToListAsync();
    }

    public async Task<ApiKey?> GetByKeyHashAsync(string keyHash)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == keyHash);
    }

    public async Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var rawKey = GenerateRawKey();
        var apiKey = new ApiKey
        {
            ApplicationId = applicationId,
            Label = label,
            KeyHash = ApiKeyHasher.Hash(rawKey),
            CreatedAt = DateTime.UtcNow
        };

        context.ApiKeys.Add(apiKey);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
        return (apiKey, rawKey);
    }

    public async Task RevokeAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var apiKey = await context.ApiKeys.FindAsync(id)
            ?? throw new InvalidOperationException($"ApiKey {id} not found.");

        if (apiKey.RevokedAt is not null)
        {
            return;
        }

        apiKey.RevokedAt = DateTime.UtcNow;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64Url = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return KeyPrefix + base64Url;
    }
}

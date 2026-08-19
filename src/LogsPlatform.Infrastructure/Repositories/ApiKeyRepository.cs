using System.Security.Cryptography;
using System.Text;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApiKeyRepository : IApiKeyRepository
{
    private const string KeyPrefix = "lgp_";

    private readonly LogsPlatformDbContext _context;

    public ApiKeyRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<ApiKey?> GetByIdAsync(int id) =>
        await _context.ApiKeys.FindAsync(id);

    public async Task<IReadOnlyList<ApiKey>> GetByApplicationIdAsync(int applicationId, bool includeRevoked = false)
    {
        var query = _context.ApiKeys.AsNoTracking().Where(k => k.ApplicationId == applicationId);
        if (!includeRevoked)
        {
            query = query.Where(k => k.RevokedAt == null);
        }
        return await query.ToListAsync();
    }

    public async Task<(ApiKey Entity, string RawKey)> AddAsync(int applicationId, string label)
    {
        var rawKey = GenerateRawKey();
        var apiKey = new ApiKey
        {
            ApplicationId = applicationId,
            Label = label,
            KeyHash = Hash(rawKey),
            CreatedAt = DateTime.UtcNow
        };

        _context.ApiKeys.Add(apiKey);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
        return (apiKey, rawKey);
    }

    public async Task RevokeAsync(int id)
    {
        var apiKey = await _context.ApiKeys.FindAsync(id)
            ?? throw new InvalidOperationException($"ApiKey {id} not found.");

        if (apiKey.RevokedAt is not null)
        {
            return;
        }

        apiKey.RevokedAt = DateTime.UtcNow;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(apiKey).State = EntityState.Detached;
            throw;
        }
    }

    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64Url = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return KeyPrefix + base64Url;
    }

    private static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }
}

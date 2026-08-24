using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface IPlatformUserRepository
{
    Task<PlatformUser?> GetByUsernameAsync(string username);
    Task<IReadOnlyList<PlatformUser>> GetAllAsync();
    Task<PlatformUser> AddAsync(PlatformUser user);
    Task DeactivateAsync(int id);
    Task<bool> AnyAsync();
}

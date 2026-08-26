namespace LogsPlatform.Domain.Repositories;

public interface IApplicationAccessGrantRepository
{
    Task<bool> HasGrantAsync(int platformUserId, int applicationId);
    Task<bool> HasAnyGrantAsync(int platformUserId);
    Task<IReadOnlyList<int>> GetGrantedApplicationIdsAsync(int platformUserId);
    Task GrantAsync(int platformUserId, int applicationId);
    Task RevokeAsync(int platformUserId, int applicationId);
}

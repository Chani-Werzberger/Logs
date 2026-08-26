using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public class ApplicationAccessService
{
    private readonly IApplicationAccessGrantRepository _grants;

    public ApplicationAccessService(IApplicationAccessGrantRepository grants)
    {
        _grants = grants;
    }

    public async Task<bool> CanManageApplicationAsync(bool isSuperAdmin, int platformUserId, int applicationId)
    {
        if (isSuperAdmin) return true;
        return await _grants.HasGrantAsync(platformUserId, applicationId);
    }
}

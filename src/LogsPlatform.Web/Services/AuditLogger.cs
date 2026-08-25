using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services;

public class AuditLogger
{
    private readonly IAuditLogRepository _repository;

    public AuditLogger(IAuditLogRepository repository)
    {
        _repository = repository;
    }

    public Task RecordAsync(int platformUserId, string entityType, string entityId, string action, string description) =>
        _repository.AddAsync(new AdminAuditLogEntry
        {
            PlatformUserId = platformUserId,
            Timestamp = DateTime.UtcNow,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Description = description
        });
}

using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record AuditLogQueryParameters(
    int? PlatformUserId,
    string? EntityType,
    string? Action,
    DateTime? From,
    DateTime? To,
    int Page,
    int PageSize);

public interface IAuditLogRepository
{
    Task<AdminAuditLogEntry> AddAsync(AdminAuditLogEntry entry);
    Task<(IReadOnlyList<AdminAuditLogEntry> Items, int TotalCount)> QueryAsync(AuditLogQueryParameters parameters);
}

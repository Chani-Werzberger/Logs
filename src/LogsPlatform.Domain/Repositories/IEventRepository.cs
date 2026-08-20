using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public record IngestResult(int Accepted, int DuplicateEventKeysSkipped);

public interface IEventRepository
{
    Task<IngestResult> AddEventsAsync(int applicationId, IReadOnlyList<Event> events);
}

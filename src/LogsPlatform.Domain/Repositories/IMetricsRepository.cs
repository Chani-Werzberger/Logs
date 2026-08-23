namespace LogsPlatform.Domain.Repositories;

public interface IMetricsRepository
{
    Task<int> GetHourlyEventCountAsync(int applicationId, int environmentId, int operationId, DateTime hourStart);
    Task<double?> GetHourlyAverageDurationAsync(int applicationId, int environmentId, int operationId, DateTime hourStart);
    Task<int> GetHourlyExceptionCountAsync(int applicationId, int environmentId, long exceptionGroupId, DateTime hourStart);
    Task<IReadOnlyList<int>> GetActiveOperationIdsAsync(int applicationId, int environmentId);
    Task<IReadOnlyList<long>> GetActiveExceptionGroupIdsAsync(int applicationId, int environmentId);
    Task<IReadOnlyDictionary<int, double>> GetCustomerRatesAsync(int applicationId, int environmentId, int? operationId, long? exceptionGroupId, DateTime windowStart);
}

using LogsPlatform.Domain.Entities;

namespace LogsPlatform.Domain.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id);
    Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false);
    Task<Customer> AddAsync(Customer customer);
    Task<Customer> RenameAsync(int id, string name);
    Task DeactivateAsync(int id);
}

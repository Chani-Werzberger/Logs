using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly LogsPlatformDbContext _context;

    public CustomerRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Customer?> GetByIdAsync(int id) =>
        await _context.Customers.FindAsync(id);

    public async Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        var query = _context.Customers.AsNoTracking().Where(c => c.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Customer> AddAsync(Customer customer)
    {
        _context.Customers.Add(customer);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task<Customer> RenameAsync(int id, string name)
    {
        var customer = await _context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.Name = name;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch
        {
            _context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task DeactivateAsync(int id)
    {
        var customer = await _context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.IsActive = false;
        await _context.SaveChangesAsync();
    }
}

using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly IDbContextFactory<LogsPlatformDbContext> _contextFactory;

    public CustomerRepository(IDbContextFactory<LogsPlatformDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<Customer?> GetByIdAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        return await context.Customers.FindAsync(id);
    }

    public async Task<IReadOnlyList<Customer>> GetByApplicationIdAsync(int applicationId, bool includeInactive = false)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var query = context.Customers.AsNoTracking().Where(c => c.ApplicationId == applicationId);
        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }
        return await query.ToListAsync();
    }

    public async Task<Customer> AddAsync(Customer customer)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        context.Customers.Add(customer);
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task<Customer> RenameAsync(int id, string name)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var customer = await context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.Name = name;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(customer).State = EntityState.Detached;
            throw;
        }
        return customer;
    }

    public async Task DeactivateAsync(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();
        var customer = await context.Customers.FindAsync(id)
            ?? throw new InvalidOperationException($"Customer {id} not found.");
        customer.IsActive = false;
        try
        {
            await context.SaveChangesAsync();
        }
        catch
        {
            context.Entry(customer).State = EntityState.Detached;
            throw;
        }
    }
}

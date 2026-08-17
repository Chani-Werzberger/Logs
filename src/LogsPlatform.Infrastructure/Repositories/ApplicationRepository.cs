using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure.Repositories;

public class ApplicationRepository : IApplicationRepository
{
    private readonly LogsPlatformDbContext _context;

    public ApplicationRepository(LogsPlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Application?> GetByIdAsync(int id) =>
        await _context.Applications.FindAsync(id);

    public async Task<IReadOnlyList<Application>> GetAllAsync() =>
        await _context.Applications.AsNoTracking().ToListAsync();

    public async Task<Application> AddAsync(Application application)
    {
        _context.Applications.Add(application);
        await _context.SaveChangesAsync();
        return application;
    }
}

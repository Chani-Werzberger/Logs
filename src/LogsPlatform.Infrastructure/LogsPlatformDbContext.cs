using LogsPlatform.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Infrastructure;

public class LogsPlatformDbContext : DbContext
{
    public LogsPlatformDbContext(DbContextOptions<LogsPlatformDbContext> options) : base(options)
    {
    }

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<AppModule> Modules => Set<AppModule>();
    public DbSet<ScreenService> ScreenServices => Set<ScreenService>();
    public DbSet<ProcessNode> Processes => Set<ProcessNode>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LogSource> LogSources => Set<LogSource>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Application>(entity =>
        {
            entity.Property(a => a.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(a => a.Name).IsUnique();
        });

        modelBuilder.Entity<AppEnvironment>(entity =>
        {
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasOne(e => e.Application)
                .WithMany(a => a.Environments)
                .HasForeignKey(e => e.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ApplicationId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<AppModule>(entity =>
        {
            entity.Property(m => m.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(m => m.Application)
                .WithMany(a => a.Modules)
                .HasForeignKey(m => m.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(m => new { m.ApplicationId, m.Name }).IsUnique();
        });

        modelBuilder.Entity<ScreenService>(entity =>
        {
            entity.Property(s => s.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(s => s.Module)
                .WithMany(m => m.ScreenServices)
                .HasForeignKey(s => s.ModuleId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => new { s.ModuleId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<ProcessNode>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(p => p.ScreenService)
                .WithMany(s => s.Processes)
                .HasForeignKey(p => p.ScreenServiceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(p => new { p.ScreenServiceId, p.Name }).IsUnique();
        });

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.Property(o => o.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(o => o.Process)
                .WithMany(p => p.Operations)
                .HasForeignKey(o => o.ProcessId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(o => new { o.ProcessId, o.Name }).IsUnique();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.ExternalCustomerId).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(c => c.Application)
                .WithMany(a => a.Customers)
                .HasForeignKey(c => c.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => new { c.ApplicationId, c.ExternalCustomerId }).IsUnique();
        });

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(u => u.ExternalUserId).HasMaxLength(200).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            entity.HasOne(u => u.Application)
                .WithMany(a => a.Users)
                .HasForeignKey(u => u.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(u => new { u.ApplicationId, u.ExternalUserId }).IsUnique();
        });

        modelBuilder.Entity<LogSource>(entity =>
        {
            entity.Property(l => l.Name).HasMaxLength(200).IsRequired();
            entity.HasOne(l => l.Application)
                .WithMany(a => a.LogSources)
                .HasForeignKey(l => l.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(l => new { l.ApplicationId, l.Name }).IsUnique();
        });
    }
}

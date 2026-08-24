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
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AppVersion> Versions => Set<AppVersion>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<ExceptionGroup> ExceptionGroups => Set<ExceptionGroup>();
    public DbSet<Baseline> Baselines => Set<Baseline>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<FindingStatement> FindingStatements => Set<FindingStatement>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<PlatformUser> PlatformUsers => Set<PlatformUser>();

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

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.Property(k => k.KeyHash).HasMaxLength(64).IsRequired();
            entity.Property(k => k.Label).HasMaxLength(200).IsRequired();
            entity.HasOne(k => k.Application)
                .WithMany(a => a.ApiKeys)
                .HasForeignKey(k => k.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(k => k.KeyHash);
        });

        modelBuilder.Entity<AppVersion>(entity =>
        {
            entity.Property(v => v.VersionNumber).HasMaxLength(200).IsRequired();
            entity.HasOne(v => v.Application)
                .WithMany(a => a.Versions)
                .HasForeignKey(v => v.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(v => new { v.ApplicationId, v.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<Deployment>(entity =>
        {
            entity.HasOne(d => d.Application)
                .WithMany(a => a.Deployments)
                .HasForeignKey(d => d.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(d => d.Environment)
                .WithMany()
                .HasForeignKey(d => d.EnvironmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(d => d.Version)
                .WithMany()
                .HasForeignKey(d => d.VersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(d => new { d.EnvironmentId, d.VersionId, d.DeployedAt });
        });

        modelBuilder.Entity<ExceptionGroup>(entity =>
        {
            entity.Property(g => g.Fingerprint).HasMaxLength(200).IsRequired();
            entity.Property(g => g.ExceptionType).HasMaxLength(500).IsRequired();
            entity.Property(g => g.MessageTemplate).HasMaxLength(1000).IsRequired();
            entity.HasOne(g => g.Application)
                .WithMany()
                .HasForeignKey(g => g.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(g => new { g.ApplicationId, g.Fingerprint }).IsUnique();
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.EventKey).HasMaxLength(100);
            entity.Property(e => e.CorrelationId).HasMaxLength(100);
            entity.Property(e => e.TraceId).HasMaxLength(100);
            entity.Property(e => e.SpanId).HasMaxLength(100);
            entity.Property(e => e.ParentSpanId).HasMaxLength(100);
            entity.Property(e => e.MessageTemplate).HasMaxLength(1000);

            entity.HasOne(e => e.Application).WithMany().HasForeignKey(e => e.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Environment).WithMany().HasForeignKey(e => e.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Version).WithMany().HasForeignKey(e => e.VersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Module).WithMany().HasForeignKey(e => e.ModuleId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ScreenService).WithMany().HasForeignKey(e => e.ScreenServiceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Process).WithMany().HasForeignKey(e => e.ProcessId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Operation).WithMany().HasForeignKey(e => e.OperationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AppUser).WithMany().HasForeignKey(e => e.AppUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.ExceptionGroup).WithMany().HasForeignKey(e => e.ExceptionGroupId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.ApplicationId, e.EnvironmentId, e.Timestamp });
            entity.HasIndex(e => new { e.ApplicationId, e.OperationId, e.Timestamp });
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => e.TraceId);
            entity.HasIndex(e => e.ExceptionGroupId);
            entity.HasIndex(e => new { e.ApplicationId, e.EventKey }).IsUnique().HasFilter("[EventKey] IS NOT NULL");
        });

        modelBuilder.Entity<Baseline>(entity =>
        {
            entity.HasOne(b => b.Application).WithMany().HasForeignKey(b => b.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(b => b.Environment).WithMany().HasForeignKey(b => b.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(b => new { b.ApplicationId, b.EnvironmentId, b.ScopeType, b.ScopeId, b.MetricType, b.BucketHourOfDay }).IsUnique();
        });

        modelBuilder.Entity<Finding>(entity =>
        {
            entity.Property(f => f.Title).HasMaxLength(500).IsRequired();
            entity.HasOne(f => f.Application).WithMany().HasForeignKey(f => f.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(f => f.Environment).WithMany().HasForeignKey(f => f.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(f => new { f.ApplicationId, f.EnvironmentId, f.ScopeType, f.ScopeId, f.Type, f.Status });
        });

        modelBuilder.Entity<FindingStatement>(entity =>
        {
            entity.Property(s => s.Text).HasMaxLength(2000).IsRequired();
            entity.Property(s => s.ApprovedBy).HasMaxLength(200);
            entity.HasOne(s => s.Finding).WithMany().HasForeignKey(s => s.FindingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(s => new { s.FindingId, s.OrderIndex });
        });

        modelBuilder.Entity<Evidence>(entity =>
        {
            entity.Property(e => e.Description).HasMaxLength(1000).IsRequired();
            entity.HasOne(e => e.Finding).WithMany().HasForeignKey(e => e.FindingId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.FindingId);
        });

        modelBuilder.Entity<PlatformUser>(entity =>
        {
            entity.Property(u => u.Username).HasMaxLength(200).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
        });
    }
}

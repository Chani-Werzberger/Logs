using System.Security.Cryptography;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Components;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, options => { });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy("RequireAdmin", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim("IsAdmin", "true"));
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));

// Repositories used by pages that render multiple DB-backed child components concurrently
// (e.g. ApplicationsAdmin.razor's expanded row) need their own short-lived DbContext per call,
// since Blazor Server's circuit-scoped DbContext throws "a second operation was started on this
// context instance before a previous operation completed" when siblings' OnInitializedAsync race.
builder.Services.AddDbContextFactory<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")),
    lifetime: ServiceLifetime.Scoped);

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IPlatformUserRepository, PlatformUserRepository>();
builder.Services.AddScoped<IAppEnvironmentRepository, AppEnvironmentRepository>();
builder.Services.AddScoped<IAppModuleRepository, AppModuleRepository>();
builder.Services.AddScoped<IScreenServiceRepository, ScreenServiceRepository>();
builder.Services.AddScoped<IProcessNodeRepository, ProcessNodeRepository>();
builder.Services.AddScoped<IOperationRepository, OperationRepository>();
builder.Services.AddScoped<BreadcrumbBuilder>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IAppUserRepository, AppUserRepository>();
builder.Services.AddScoped<ILogSourceRepository, LogSourceRepository>();
builder.Services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<IAppVersionRepository, AppVersionRepository>();
builder.Services.AddScoped<IDeploymentRepository, DeploymentRepository>();
builder.Services.AddScoped<IEventRepository, EventRepository>();
builder.Services.AddScoped<IExceptionGroupRepository, ExceptionGroupRepository>();
builder.Services.AddScoped<HierarchyResolver>();
builder.Services.AddScoped<IngestionProcessor>();
builder.Services.AddScoped<IMetricsRepository, MetricsRepository>();
builder.Services.AddScoped<IBaselineRepository, BaselineRepository>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.BaselineCalculator>();
builder.Services.AddScoped<IFindingRepository, FindingRepository>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.FindingWriter>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.RateAnomalyDetector>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.NewExceptionDetector>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.CustomerOutlierDetector>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DeploymentCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.DownstreamFailureCorrelator>();
builder.Services.AddScoped<LogsPlatform.Web.Services.Analysis.AnalysisEngineTickRunner>();
builder.Services.AddHostedService<LogsPlatform.Web.Services.Analysis.AnalysisEngineBackgroundService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var platformUsers = scope.ServiceProvider.GetRequiredService<IPlatformUserRepository>();
    if (!await platformUsers.AnyAsync())
    {
        var generatedPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
        await platformUsers.AddAsync(new PlatformUser
        {
            Username = "admin",
            PasswordHash = PasswordHasher.Hash(generatedPassword),
            IsAdmin = true,
            CreatedAt = DateTime.UtcNow
        });
        Console.WriteLine("=================================================================");
        Console.WriteLine("No PlatformUser exists yet — seeded a default admin account:");
        Console.WriteLine($"  Username: admin");
        Console.WriteLine($"  Password: {generatedPassword}");
        Console.WriteLine("This password is shown once and is not stored anywhere else.");
        Console.WriteLine("=================================================================");
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program
{
} // exposes Program for WebApplicationFactory<Program> in tests

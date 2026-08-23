using LogsPlatform.Domain.Repositories;
using LogsPlatform.Infrastructure;
using LogsPlatform.Infrastructure.Repositories;
using LogsPlatform.Web.Authentication;
using LogsPlatform.Web.Components;
using LogsPlatform.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(ApiKeyAuthenticationOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, options => { });
builder.Services.AddAuthorization();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LogsPlatformDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LogsPlatformDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:LogsPlatformDb configuration.")));

builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
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

var app = builder.Build();

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

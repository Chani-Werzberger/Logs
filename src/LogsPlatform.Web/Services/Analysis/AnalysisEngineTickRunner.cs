using LogsPlatform.Domain.Repositories;

namespace LogsPlatform.Web.Services.Analysis;

public class AnalysisEngineTickRunner
{
    private static readonly TimeSpan TickLookback = TimeSpan.FromMinutes(5);

    private readonly IApplicationRepository _applications;
    private readonly IAppEnvironmentRepository _environments;
    private readonly IBaselineRepository _baselines;
    private readonly IFindingRepository _findings;
    private readonly BaselineCalculator _baselineCalculator;
    private readonly RateAnomalyDetector _rateAnomalyDetector;
    private readonly NewExceptionDetector _newExceptionDetector;
    private readonly CustomerOutlierDetector _customerOutlierDetector;
    private readonly DeploymentCorrelator _deploymentCorrelator;

    public AnalysisEngineTickRunner(
        IApplicationRepository applications,
        IAppEnvironmentRepository environments,
        IBaselineRepository baselines,
        IFindingRepository findings,
        BaselineCalculator baselineCalculator,
        RateAnomalyDetector rateAnomalyDetector,
        NewExceptionDetector newExceptionDetector,
        CustomerOutlierDetector customerOutlierDetector,
        DeploymentCorrelator deploymentCorrelator)
    {
        _applications = applications;
        _environments = environments;
        _baselines = baselines;
        _findings = findings;
        _baselineCalculator = baselineCalculator;
        _rateAnomalyDetector = rateAnomalyDetector;
        _newExceptionDetector = newExceptionDetector;
        _customerOutlierDetector = customerOutlierDetector;
        _deploymentCorrelator = deploymentCorrelator;
    }

    public async Task RunOneTickAsync()
    {
        var applications = await _applications.GetAllAsync();
        foreach (var application in applications)
        {
            var environments = await _environments.GetByApplicationIdAsync(application.Id);
            foreach (var environment in environments)
            {
                await RunForApplicationEnvironmentAsync(application.Id, environment.Id);
            }
        }
    }

    private async Task RunForApplicationEnvironmentAsync(int applicationId, int environmentId)
    {
        if (!await _baselines.HasUpdatedTodayAsync(applicationId, environmentId))
        {
            await _baselineCalculator.RunAsync(applicationId, environmentId);
        }

        var tickStart = DateTime.UtcNow;

        await _rateAnomalyDetector.RunAsync(applicationId, environmentId);
        await _newExceptionDetector.RunAsync(applicationId, environmentId);
        await _customerOutlierDetector.RunAsync(applicationId, environmentId);

        var newFindings = await _findings.GetDetectedSinceAsync(applicationId, environmentId, tickStart - TickLookback);
        foreach (var finding in newFindings)
        {
            await _deploymentCorrelator.RunAsync(finding);
        }
    }
}

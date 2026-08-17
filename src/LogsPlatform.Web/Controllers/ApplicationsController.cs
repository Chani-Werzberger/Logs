using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications")]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationRepository _applications;

    public ApplicationsController(IApplicationRepository applications)
    {
        _applications = applications;
    }

    [HttpPost]
    public async Task<ActionResult<ApplicationResponse>> Create(CreateApplicationRequest request)
    {
        var application = await _applications.AddAsync(new Application
        {
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        });

        var response = new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt);
        return CreatedAtAction(nameof(GetById), new { id = application.Id }, response);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApplicationResponse>> GetById(int id)
    {
        var application = await _applications.GetByIdAsync(id);
        if (application is null) return NotFound();
        return new ApplicationResponse(application.Id, application.Name, application.Description, application.CreatedAt);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApplicationResponse>>> GetAll()
    {
        var applications = await _applications.GetAllAsync();
        return applications
            .Select(a => new ApplicationResponse(a.Id, a.Name, a.Description, a.CreatedAt))
            .ToList();
    }
}

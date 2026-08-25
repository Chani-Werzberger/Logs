using System.Security.Claims;
using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
using LogsPlatform.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LogsPlatform.Web.Controllers;

[ApiController]
[Route("api/v1/admin/applications/{appId:int}/customers")]
[Authorize(Policy = "RequireAdmin")]
public class CustomersController : ControllerBase
{
    private readonly IApplicationRepository _applications;
    private readonly ICustomerRepository _customers;
    private readonly AuditLogger _audit;

    public CustomersController(IApplicationRepository applications, ICustomerRepository customers, AuditLogger audit)
    {
        _applications = applications;
        _customers = customers;
        _audit = audit;
    }

    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> Create(int appId, CreateCustomerRequest request)
    {
        if (await _applications.GetByIdAsync(appId) is null)
        {
            return NotFound(new { message = $"Application {appId} not found." });
        }

        try
        {
            var customer = await _customers.AddAsync(new Customer
            {
                ApplicationId = appId,
                ExternalCustomerId = request.ExternalCustomerId,
                Name = request.Name
            });

            var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            await _audit.RecordAsync(platformUserId, "Customer", customer.Id.ToString(), "Create", $"Created customer '{customer.Name}' (external id '{customer.ExternalCustomerId}') in application {appId}");

            return CreatedAtAction(nameof(GetById), new { appId, id = customer.Id }, ToResponse(customer));
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Conflict(new { message = $"A customer with external id '{request.ExternalCustomerId}' already exists in this application." });
        }
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerResponse>> GetById(int appId, int id)
    {
        var customer = await _customers.GetByIdAsync(id);
        if (customer is null || customer.ApplicationId != appId) return NotFound();
        return ToResponse(customer);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CustomerResponse>>> GetAll(int appId, [FromQuery] bool includeInactive = false)
    {
        var customers = await _customers.GetByApplicationIdAsync(appId, includeInactive);
        return customers.Select(ToResponse).ToList();
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CustomerResponse>> Rename(int appId, int id, RenameCustomerRequest request)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        var customer = await _customers.RenameAsync(id, request.Name);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Update", $"Renamed customer {id} to '{request.Name}' in application {appId}");

        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _customers.DeactivateAsync(id);

        var platformUserId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await _audit.RecordAsync(platformUserId, "Customer", id.ToString(), "Deactivate", $"Deactivated customer {id} in application {appId}");

        return NoContent();
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.ApplicationId, customer.ExternalCustomerId, customer.Name, customer.IsActive);
}

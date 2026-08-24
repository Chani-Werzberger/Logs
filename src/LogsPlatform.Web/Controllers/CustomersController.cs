using LogsPlatform.Domain.Entities;
using LogsPlatform.Domain.Repositories;
using LogsPlatform.Web.Contracts;
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

    public CustomersController(IApplicationRepository applications, ICustomerRepository customers)
    {
        _applications = applications;
        _customers = customers;
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
        return ToResponse(customer);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int appId, int id)
    {
        var existing = await _customers.GetByIdAsync(id);
        if (existing is null || existing.ApplicationId != appId) return NotFound();

        await _customers.DeactivateAsync(id);
        return NoContent();
    }

    private static CustomerResponse ToResponse(Customer customer) =>
        new(customer.Id, customer.ApplicationId, customer.ExternalCustomerId, customer.Name, customer.IsActive);
}

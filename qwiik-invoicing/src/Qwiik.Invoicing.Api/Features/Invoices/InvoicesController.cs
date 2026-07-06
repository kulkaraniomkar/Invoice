using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Qwiik.Invoicing.Api.Contracts;

namespace Qwiik.Invoicing.Api.Features.Invoices;

/// <summary>
/// Thin HTTP layer: validates request shape, delegates to the service, maps to status codes.
/// All endpoints are tenant-scoped via TenantResolutionMiddleware + the EF global query filter.
/// </summary>
[ApiController]
[Route("api/invoices")]
[Produces("application/json")]
public sealed class InvoicesController(
    IInvoiceService invoiceService,
    IValidator<CreateInvoiceRequest> createValidator) : ControllerBase
{
    /// <summary>Creates a new invoice in Draft status.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvoiceResponse>> Create(CreateInvoiceRequest request, CancellationToken ct)
    {
        var validation = await createValidator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var invoice = await invoiceService.CreateAsync(request, ct);
        return CreatedAtAction(nameof(GetById), new { id = invoice.Id }, invoice);
    }

    /// <summary>Lists invoices with pagination, filtering (status, search, issue-date range) and sorting.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<InvoiceListItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<InvoiceListItemResponse>>> List(
        [FromQuery] ListInvoicesQuery query, CancellationToken ct)
        => Ok(await invoiceService.ListAsync(query, ct));

    /// <summary>Returns a single invoice including its line items.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvoiceResponse>> GetById(Guid id, CancellationToken ct)
        => Ok(await invoiceService.GetByIdAsync(id, ct));

    /// <summary>
    /// Updates the invoice status. Allowed: Draft→Sent, Draft→Cancelled, Sent→Paid, Sent→Cancelled.
    /// Illegal transitions return 422; concurrent updates return 409.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvoiceResponse>> UpdateStatus(
        Guid id, UpdateInvoiceStatusRequest request, CancellationToken ct)
        => Ok(await invoiceService.UpdateStatusAsync(id, request.Status, ct));

    /// <summary>Dashboard summary: counts and amounts by status, outstanding, paid and overdue.</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(InvoiceSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvoiceSummaryResponse>> GetSummary(CancellationToken ct)
        => Ok(await invoiceService.GetSummaryAsync(ct));
}

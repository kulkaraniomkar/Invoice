using Microsoft.EntityFrameworkCore;
using Qwiik.Invoicing.Api.Contracts;
using Qwiik.Invoicing.Api.Domain;
using Qwiik.Invoicing.Api.Infrastructure;

namespace Qwiik.Invoicing.Api.Features.Invoices;

public interface IInvoiceService
{
    Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, CancellationToken ct);
    Task<PagedResponse<InvoiceListItemResponse>> ListAsync(ListInvoicesQuery query, CancellationToken ct);
    Task<InvoiceResponse> GetByIdAsync(Guid id, CancellationToken ct);
    Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus targetStatus, CancellationToken ct);
    Task<InvoiceSummaryResponse> GetSummaryAsync(CancellationToken ct);
}

public sealed class InvoiceService(InvoicingDbContext db, ILogger<InvoiceService> logger) : IInvoiceService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public async Task<InvoiceResponse> CreateAsync(CreateInvoiceRequest request, CancellationToken ct)
    {
        var lineItems = request.LineItems
            .Select(li => new InvoiceLineItem(li.Description, li.Quantity, li.UnitPrice))
            .ToList();

        var invoiceNumber = await GenerateUniqueInvoiceNumberAsync(ct);

        var invoice = Invoice.Create(
            invoiceNumber,
            request.CustomerName,
            request.CustomerEmail,
            request.Currency,
            request.IssueDate,
            request.DueDate,
            request.TaxRate,
            request.Notes,
            lineItems);

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct); // TenantId, timestamps and concurrency token stamped here

        logger.LogInformation("Created invoice {InvoiceNumber} ({InvoiceId}) with total {Total} {Currency}",
            invoice.InvoiceNumber, invoice.Id, invoice.Total, invoice.Currency);

        return ToResponse(invoice);
    }

    public async Task<PagedResponse<InvoiceListItemResponse>> ListAsync(ListInvoicesQuery query, CancellationToken ct)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize is <= 0 ? DefaultPageSize : query.PageSize, 1, MaxPageSize);

        // Tenant filtering is applied automatically by the global query filter.
        var invoices = db.Invoices.AsNoTracking();

        if (query.Status.HasValue)
            invoices = invoices.Where(i => i.Status == query.Status.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            invoices = invoices.Where(i =>
                i.InvoiceNumber.Contains(term) || i.CustomerName.Contains(term));
        }

        if (query.IssuedFrom.HasValue)
            invoices = invoices.Where(i => i.IssueDate >= query.IssuedFrom.Value);

        if (query.IssuedTo.HasValue)
            invoices = invoices.Where(i => i.IssueDate <= query.IssuedTo.Value);

        var totalCount = await invoices.CountAsync(ct);

        invoices = ApplySorting(invoices, query.SortBy, query.SortDirection);

        var today = Today();
        var items = await invoices
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new InvoiceListItemResponse(
                i.Id,
                i.InvoiceNumber,
                i.CustomerName,
                i.Currency,
                i.IssueDate,
                i.DueDate,
                i.Status,
                i.Status == InvoiceStatus.Sent && i.DueDate < today,
                i.Total,
                i.CreatedAtUtc))
            .ToListAsync(ct);

        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PagedResponse<InvoiceListItemResponse>(items, page, pageSize, totalCount, totalPages);
    }

    public async Task<InvoiceResponse> GetByIdAsync(Guid id, CancellationToken ct)
    {
        // Owned line items are loaded automatically with the aggregate.
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Invoice '{id}' was not found.");

        return ToResponse(invoice);
    }

    public async Task<InvoiceResponse> UpdateStatusAsync(Guid id, InvoiceStatus targetStatus, CancellationToken ct)
    {
        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException($"Invoice '{id}' was not found.");

        var previousStatus = invoice.Status;
        invoice.TransitionTo(targetStatus); // throws DomainException (422) on an illegal transition
        await db.SaveChangesAsync(ct);      // throws DbUpdateConcurrencyException (409) on a lost race

        logger.LogInformation("Invoice {InvoiceNumber} ({InvoiceId}) moved from {From} to {To}",
            invoice.InvoiceNumber, invoice.Id, previousStatus, targetStatus);

        return ToResponse(invoice);
    }

    public async Task<InvoiceSummaryResponse> GetSummaryAsync(CancellationToken ct)
    {
        var today = Today();

        // One grouped aggregate over the (TenantId, Status) index instead of N queries.
        var statusGroups = await db.Invoices.AsNoTracking()
            .GroupBy(i => i.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(i => i.Total) })
            .ToListAsync(ct);

        var overdueCount = await db.Invoices.AsNoTracking()
            .CountAsync(i => i.Status == InvoiceStatus.Sent && i.DueDate < today, ct);

        var overdueAmount = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < today)
            .SumAsync(i => (decimal?)i.Total, ct) ?? 0m;

        decimal AmountFor(InvoiceStatus status) =>
            statusGroups.FirstOrDefault(g => g.Status == status)?.Amount ?? 0m;

        var byStatus = Enum.GetValues<InvoiceStatus>()
            .Select(s => new StatusSummary(
                s,
                statusGroups.FirstOrDefault(g => g.Status == s)?.Count ?? 0,
                AmountFor(s)))
            .ToList();

        return new InvoiceSummaryResponse(
            TotalInvoices: statusGroups.Sum(g => g.Count),
            TotalInvoicedAmount: statusGroups.Where(g => g.Status != InvoiceStatus.Cancelled).Sum(g => g.Amount),
            OutstandingAmount: AmountFor(InvoiceStatus.Sent),
            PaidAmount: AmountFor(InvoiceStatus.Paid),
            Overdue: new OverdueSummary(overdueCount, overdueAmount),
            ByStatus: byStatus);
    }

    private static IQueryable<Invoice> ApplySorting(IQueryable<Invoice> source, string? sortBy, string? direction)
    {
        var descending = !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);

        // Whitelisted sort columns only — never build expressions from raw user input.
        return (sortBy?.ToLowerInvariant(), descending) switch
        {
            ("issuedate", false) => source.OrderBy(i => i.IssueDate).ThenBy(i => i.Id),
            ("issuedate", true) => source.OrderByDescending(i => i.IssueDate).ThenBy(i => i.Id),
            ("duedate", false) => source.OrderBy(i => i.DueDate).ThenBy(i => i.Id),
            ("duedate", true) => source.OrderByDescending(i => i.DueDate).ThenBy(i => i.Id),
            ("total", false) => source.OrderBy(i => i.Total).ThenBy(i => i.Id),
            ("total", true) => source.OrderByDescending(i => i.Total).ThenBy(i => i.Id),
            ("customername", false) => source.OrderBy(i => i.CustomerName).ThenBy(i => i.Id),
            ("customername", true) => source.OrderByDescending(i => i.CustomerName).ThenBy(i => i.Id),
            (_, false) => source.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id),
            _ => source.OrderByDescending(i => i.CreatedAtUtc).ThenBy(i => i.Id)
        };
    }

    /// <summary>
    /// Generates e.g. "INV-20260705-4K7QZ1". Uniqueness per tenant is enforced by a unique
    /// index; the pre-check plus 26^6-style randomness makes collisions effectively impossible,
    /// and the index guarantees correctness even under a race.
    /// </summary>
    private async Task<string> GenerateUniqueInvoiceNumberAsync(CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var candidate = $"INV-{DateTime.UtcNow:yyyyMMdd}-{RandomSuffix(6)}";
            // The global query filter scopes this existence check to the current tenant.
            if (!await db.Invoices.AnyAsync(i => i.InvoiceNumber == candidate, ct))
                return candidate;
        }

        throw new InvalidOperationException("Failed to generate a unique invoice number.");
    }

    private static string RandomSuffix(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I to keep numbers readable
        return string.Create(length, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static InvoiceResponse ToResponse(Invoice invoice) => new(
        invoice.Id,
        invoice.InvoiceNumber,
        invoice.CustomerName,
        invoice.CustomerEmail,
        invoice.Currency,
        invoice.IssueDate,
        invoice.DueDate,
        invoice.Status,
        invoice.IsOverdue(Today()),
        invoice.TaxRate,
        invoice.Subtotal,
        invoice.TaxAmount,
        invoice.Total,
        invoice.Notes,
        invoice.LineItems
            .Select(li => new InvoiceLineItemResponse(li.Id, li.Description, li.Quantity, li.UnitPrice, li.LineTotal))
            .ToList(),
        invoice.CreatedAtUtc,
        invoice.UpdatedAtUtc);
}

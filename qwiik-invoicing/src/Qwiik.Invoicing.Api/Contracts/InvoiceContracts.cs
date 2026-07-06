using Qwiik.Invoicing.Api.Domain;

namespace Qwiik.Invoicing.Api.Contracts;

// ---------- Requests ----------

public sealed record CreateInvoiceRequest(
    string CustomerName,
    string? CustomerEmail,
    string Currency,
    DateOnly IssueDate,
    DateOnly DueDate,
    decimal TaxRate,
    string? Notes,
    List<CreateInvoiceLineItemRequest> LineItems);

public sealed record CreateInvoiceLineItemRequest(
    string Description,
    decimal Quantity,
    decimal UnitPrice);

public sealed record UpdateInvoiceStatusRequest(InvoiceStatus Status);

/// <summary>Query string parameters for listing invoices. Page size is clamped server-side.</summary>
public sealed record ListInvoicesQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public InvoiceStatus? Status { get; init; }

    /// <summary>Matches against invoice number or customer name (contains).</summary>
    public string? Search { get; init; }

    public DateOnly? IssuedFrom { get; init; }
    public DateOnly? IssuedTo { get; init; }

    /// <summary>issueDate | dueDate | total | customerName | createdAt (default).</summary>
    public string? SortBy { get; init; }

    /// <summary>asc | desc (default).</summary>
    public string? SortDirection { get; init; }
}

// ---------- Responses ----------

public sealed record InvoiceResponse(
    Guid Id,
    string InvoiceNumber,
    string CustomerName,
    string? CustomerEmail,
    string Currency,
    DateOnly IssueDate,
    DateOnly DueDate,
    InvoiceStatus Status,
    bool IsOverdue,
    decimal TaxRate,
    decimal Subtotal,
    decimal TaxAmount,
    decimal Total,
    string? Notes,
    IReadOnlyList<InvoiceLineItemResponse> LineItems,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record InvoiceLineItemResponse(
    Guid Id,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>Slim projection used for list results — line items are not loaded.</summary>
public sealed record InvoiceListItemResponse(
    Guid Id,
    string InvoiceNumber,
    string CustomerName,
    string Currency,
    DateOnly IssueDate,
    DateOnly DueDate,
    InvoiceStatus Status,
    bool IsOverdue,
    decimal Total,
    DateTime CreatedAtUtc);

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record InvoiceSummaryResponse(
    int TotalInvoices,
    decimal TotalInvoicedAmount,
    decimal OutstandingAmount,
    decimal PaidAmount,
    OverdueSummary Overdue,
    IReadOnlyList<StatusSummary> ByStatus);

public sealed record StatusSummary(InvoiceStatus Status, int Count, decimal Amount);

public sealed record OverdueSummary(int Count, decimal Amount);

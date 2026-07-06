namespace Qwiik.Invoicing.Api.Domain;

/// <summary>
/// Aggregate root for an invoice. All state changes go through methods on this class
/// so business rules (status lifecycle, totals) cannot be bypassed from the outside.
/// </summary>
public class Invoice
{
    // Allowed status transitions. "Overdue" is deliberately NOT a stored status:
    // it is derived from (Status == Sent && DueDate < today), which avoids a
    // background job and the risk of stored state drifting from reality.
    private static readonly IReadOnlyDictionary<InvoiceStatus, InvoiceStatus[]> AllowedTransitions =
        new Dictionary<InvoiceStatus, InvoiceStatus[]>
        {
            [InvoiceStatus.Draft] = [InvoiceStatus.Sent, InvoiceStatus.Cancelled],
            [InvoiceStatus.Sent] = [InvoiceStatus.Paid, InvoiceStatus.Cancelled],
            [InvoiceStatus.Paid] = [],
            [InvoiceStatus.Cancelled] = []
        };

    private readonly List<InvoiceLineItem> _lineItems = [];

    public Guid Id { get; private set; }

    /// <summary>Owning tenant. Stamped by the DbContext from the ambient tenant; never accepted from clients.</summary>
    public Guid TenantId { get; internal set; }

    /// <summary>Human-readable, unique per tenant (enforced by a unique index).</summary>
    public string InvoiceNumber { get; private set; } = null!;

    public string CustomerName { get; private set; } = null!;
    public string? CustomerEmail { get; private set; }

    /// <summary>ISO 4217 code, e.g. "USD".</summary>
    public string Currency { get; private set; } = null!;

    public DateOnly IssueDate { get; private set; }
    public DateOnly DueDate { get; private set; }

    /// <summary>Tax rate in percent (0–100), applied to the subtotal.</summary>
    public decimal TaxRate { get; private set; }

    public string? Notes { get; private set; }

    public InvoiceStatus Status { get; private set; }

    // Monetary totals are computed in the domain and persisted, so list/summary
    // queries never need to join line items or recompute.
    public decimal Subtotal { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal Total { get; private set; }

    public DateTime CreatedAtUtc { get; internal set; }
    public DateTime UpdatedAtUtc { get; internal set; }

    /// <summary>Optimistic concurrency token, rotated by the DbContext on every write.</summary>
    public Guid ConcurrencyToken { get; internal set; }

    public IReadOnlyCollection<InvoiceLineItem> LineItems => _lineItems.AsReadOnly();

    private Invoice() { } // EF Core

    public static Invoice Create(
        string invoiceNumber,
        string customerName,
        string? customerEmail,
        string currency,
        DateOnly issueDate,
        DateOnly dueDate,
        decimal taxRate,
        string? notes,
        IReadOnlyCollection<InvoiceLineItem> lineItems)
    {
        // The API layer validates requests with FluentValidation; these guards exist
        // so the invariants also hold for any future (non-HTTP) code path.
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new DomainException("Invoice number is required.");
        if (string.IsNullOrWhiteSpace(customerName))
            throw new DomainException("Customer name is required.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
            throw new DomainException("Currency must be a 3-letter ISO 4217 code.");
        if (dueDate < issueDate)
            throw new DomainException("Due date cannot be earlier than the issue date.");
        if (taxRate is < 0 or > 100)
            throw new DomainException("Tax rate must be between 0 and 100 percent.");
        if (lineItems is null || lineItems.Count == 0)
            throw new DomainException("An invoice must contain at least one line item.");

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            InvoiceNumber = invoiceNumber.Trim(),
            CustomerName = customerName.Trim(),
            CustomerEmail = string.IsNullOrWhiteSpace(customerEmail) ? null : customerEmail.Trim(),
            Currency = currency.Trim().ToUpperInvariant(),
            IssueDate = issueDate,
            DueDate = dueDate,
            TaxRate = taxRate,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Status = InvoiceStatus.Draft
        };

        invoice._lineItems.AddRange(lineItems);
        invoice.RecalculateTotals();
        return invoice;
    }

    /// <summary>
    /// Moves the invoice to <paramref name="target"/> if the lifecycle allows it.
    /// Draft → Sent | Cancelled; Sent → Paid | Cancelled; Paid and Cancelled are terminal.
    /// </summary>
    public void TransitionTo(InvoiceStatus target)
    {
        if (target == Status)
            throw new DomainException($"Invoice is already in status '{Status}'.");

        var allowed = AllowedTransitions[Status];
        if (!allowed.Contains(target))
        {
            var allowedText = allowed.Length == 0
                ? $"'{Status}' is a terminal status"
                : $"allowed transitions from '{Status}' are: {string.Join(", ", allowed)}";
            throw new DomainException($"Cannot change invoice status from '{Status}' to '{target}' ({allowedText}).");
        }

        Status = target;
    }

    /// <summary>Derived state: a sent invoice whose due date has passed.</summary>
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Sent && DueDate < today;

    private void RecalculateTotals()
    {
        Subtotal = Math.Round(_lineItems.Sum(li => li.LineTotal), 2, MidpointRounding.AwayFromZero);
        TaxAmount = Math.Round(Subtotal * TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
        Total = Subtotal + TaxAmount;
    }
}

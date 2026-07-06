namespace Qwiik.Invoicing.Api.Domain;

/// <summary>
/// Owned child of <see cref="Invoice"/>. Line totals are computed and rounded here
/// (per line, away-from-zero) so the invoice total is always the sum of what the
/// customer actually sees on each line.
/// </summary>
public class InvoiceLineItem
{
    public Guid Id { get; private set; }
    public string Description { get; private set; } = null!;
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal LineTotal { get; private set; }

    private InvoiceLineItem() { } // EF Core

    public InvoiceLineItem(string description, decimal quantity, decimal unitPrice)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new DomainException("Line item description is required.");
        if (quantity <= 0)
            throw new DomainException("Line item quantity must be greater than zero.");
        if (unitPrice < 0)
            throw new DomainException("Line item unit price cannot be negative.");

        Id = Guid.NewGuid();
        Description = description.Trim();
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineTotal = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
    }
}

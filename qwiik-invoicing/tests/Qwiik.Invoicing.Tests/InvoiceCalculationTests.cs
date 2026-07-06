using Qwiik.Invoicing.Api.Domain;
using Xunit;

namespace Qwiik.Invoicing.Tests;

public class InvoiceCalculationTests
{
    [Fact]
    public void Totals_are_computed_from_line_items_and_tax_rate()
    {
        var invoice = TestData.NewInvoice(
            taxRate: 10m,
            lineItems:
            [
                new InvoiceLineItem("Design", 2, 150m),   // 300.00
                new InvoiceLineItem("Hosting", 1, 49.99m) //  49.99
            ]);

        Assert.Equal(349.99m, invoice.Subtotal);
        Assert.Equal(35.00m, invoice.TaxAmount); // 34.999 → 35.00 away-from-zero
        Assert.Equal(384.99m, invoice.Total);
    }

    [Fact]
    public void Line_totals_are_rounded_per_line_before_summing()
    {
        // 3 × 0.335 = 1.005 → 1.01 per line (away from zero).
        // Rounding per line matches what a customer sees printed on the invoice.
        var invoice = TestData.NewInvoice(
            taxRate: 0m,
            lineItems:
            [
                new InvoiceLineItem("Widget A", 3, 0.335m),
                new InvoiceLineItem("Widget B", 3, 0.335m)
            ]);

        Assert.Equal(1.01m, invoice.LineItems.First().LineTotal);
        Assert.Equal(2.02m, invoice.Subtotal);
        Assert.Equal(2.02m, invoice.Total);
    }

    [Fact]
    public void Zero_tax_rate_yields_no_tax()
    {
        var invoice = TestData.NewInvoice(taxRate: 0m,
            lineItems: [new InvoiceLineItem("Service", 1, 100m)]);

        Assert.Equal(0m, invoice.TaxAmount);
        Assert.Equal(invoice.Subtotal, invoice.Total);
    }

    // ---- Creation invariants ----

    [Fact]
    public void Invoice_requires_at_least_one_line_item()
    {
        Assert.Throws<DomainException>(() => TestData.NewInvoice(lineItems: Array.Empty<InvoiceLineItem>()));
    }

    [Fact]
    public void Due_date_cannot_precede_issue_date()
    {
        Assert.Throws<DomainException>(() => TestData.NewInvoice(
            issueDate: new DateOnly(2026, 7, 10),
            dueDate: new DateOnly(2026, 7, 9)));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Tax_rate_outside_0_to_100_is_rejected(decimal taxRate)
    {
        Assert.Throws<DomainException>(() => TestData.NewInvoice(taxRate: taxRate));
    }

    [Fact]
    public void Line_item_rejects_non_positive_quantity_and_negative_price()
    {
        Assert.Throws<DomainException>(() => new InvoiceLineItem("x", 0, 10m));
        Assert.Throws<DomainException>(() => new InvoiceLineItem("x", -1, 10m));
        Assert.Throws<DomainException>(() => new InvoiceLineItem("x", 1, -0.01m));
    }

    [Fact]
    public void Currency_is_normalised_to_uppercase()
    {
        var invoice = Invoice.Create(
            "INV-X", "Acme", null, "usd",
            new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31),
            0m, null, [new InvoiceLineItem("Service", 1, 10m)]);

        Assert.Equal("USD", invoice.Currency);
    }
}

using Qwiik.Invoicing.Api.Domain;

namespace Qwiik.Invoicing.Tests;

/// <summary>Factory helpers for building valid domain objects in tests.</summary>
public static class TestData
{
    public static Invoice NewInvoice(
        string invoiceNumber = "INV-20260701-TEST01",
        string customerName = "Acme Corp",
        decimal taxRate = 10m,
        DateOnly? issueDate = null,
        DateOnly? dueDate = null,
        IReadOnlyCollection<InvoiceLineItem>? lineItems = null)
        => Invoice.Create(
            invoiceNumber,
            customerName,
            "billing@acme.test",
            "USD",
            issueDate ?? new DateOnly(2026, 7, 1),
            dueDate ?? new DateOnly(2026, 7, 31),
            taxRate,
            notes: null,
            lineItems ?? [new InvoiceLineItem("Consulting", 2, 100m)]);

    /// <summary>Walks the legal lifecycle to reach the requested status.</summary>
    public static Invoice InvoiceInStatus(InvoiceStatus status)
    {
        var invoice = NewInvoice();
        switch (status)
        {
            case InvoiceStatus.Draft:
                break;
            case InvoiceStatus.Sent:
                invoice.TransitionTo(InvoiceStatus.Sent);
                break;
            case InvoiceStatus.Paid:
                invoice.TransitionTo(InvoiceStatus.Sent);
                invoice.TransitionTo(InvoiceStatus.Paid);
                break;
            case InvoiceStatus.Cancelled:
                invoice.TransitionTo(InvoiceStatus.Cancelled);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
        return invoice;
    }
}

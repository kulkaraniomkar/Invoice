using Qwiik.Invoicing.Api.Domain;
using Xunit;

namespace Qwiik.Invoicing.Tests;

/// <summary>
/// The status lifecycle is the core business rule of the module:
/// Draft → Sent | Cancelled; Sent → Paid | Cancelled; Paid and Cancelled are terminal.
/// </summary>
public class InvoiceStatusTransitionTests
{
    [Theory]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Sent)]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Cancelled)]
    [InlineData(InvoiceStatus.Sent, InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Sent, InvoiceStatus.Cancelled)]
    public void Allowed_transitions_succeed(InvoiceStatus from, InvoiceStatus to)
    {
        var invoice = TestData.InvoiceInStatus(from);

        invoice.TransitionTo(to);

        Assert.Equal(to, invoice.Status);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft, InvoiceStatus.Paid)]       // cannot pay an unsent invoice
    [InlineData(InvoiceStatus.Sent, InvoiceStatus.Draft)]       // cannot un-send
    [InlineData(InvoiceStatus.Paid, InvoiceStatus.Sent)]        // Paid is terminal
    [InlineData(InvoiceStatus.Paid, InvoiceStatus.Cancelled)]   // cannot cancel a paid invoice
    [InlineData(InvoiceStatus.Cancelled, InvoiceStatus.Sent)]   // Cancelled is terminal
    [InlineData(InvoiceStatus.Cancelled, InvoiceStatus.Paid)]
    public void Illegal_transitions_throw(InvoiceStatus from, InvoiceStatus to)
    {
        var invoice = TestData.InvoiceInStatus(from);

        var ex = Assert.Throws<DomainException>(() => invoice.TransitionTo(to));

        Assert.Equal(from, invoice.Status); // state unchanged after the failed transition
        Assert.Contains(from.ToString(), ex.Message);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Sent)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Transition_to_same_status_throws(InvoiceStatus status)
    {
        var invoice = TestData.InvoiceInStatus(status);

        Assert.Throws<DomainException>(() => invoice.TransitionTo(status));
    }

    [Fact]
    public void New_invoices_start_in_draft()
    {
        Assert.Equal(InvoiceStatus.Draft, TestData.NewInvoice().Status);
    }

    // ---- Overdue is derived, never stored ----

    [Fact]
    public void Sent_invoice_past_due_date_is_overdue()
    {
        var invoice = TestData.InvoiceInStatus(InvoiceStatus.Sent);

        Assert.True(invoice.IsOverdue(today: invoice.DueDate.AddDays(1)));
    }

    [Fact]
    public void Sent_invoice_on_or_before_due_date_is_not_overdue()
    {
        var invoice = TestData.InvoiceInStatus(InvoiceStatus.Sent);

        Assert.False(invoice.IsOverdue(today: invoice.DueDate));
        Assert.False(invoice.IsOverdue(today: invoice.DueDate.AddDays(-1)));
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void Non_sent_invoices_are_never_overdue(InvoiceStatus status)
    {
        var invoice = TestData.InvoiceInStatus(status);

        Assert.False(invoice.IsOverdue(today: invoice.DueDate.AddDays(365)));
    }
}

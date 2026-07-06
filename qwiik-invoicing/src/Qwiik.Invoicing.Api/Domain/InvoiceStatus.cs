namespace Qwiik.Invoicing.Api.Domain;

/// <summary>
/// Stored invoice lifecycle. Note: "Overdue" is intentionally not a stored status;
/// it is derived from Sent + DueDate &lt; today (see <see cref="Invoice.IsOverdue"/>),
/// so it never needs a scheduled job to keep it accurate.
/// </summary>
public enum InvoiceStatus
{
    Draft = 0,
    Sent = 1,
    Paid = 2,
    Cancelled = 3
}

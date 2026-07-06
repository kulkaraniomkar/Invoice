using FluentValidation;
using Qwiik.Invoicing.Api.Contracts;

namespace Qwiik.Invoicing.Api.Validation;

/// <summary>
/// Request-shape validation (lengths, ranges, formats) lives here and returns field-level
/// 400 responses. Business rules that must always hold (status lifecycle, totals) live in
/// the domain model, so they cannot be bypassed by a future non-HTTP entry point.
/// </summary>
public sealed class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequest>
{
    public const int MaxLineItems = 100;

    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.CustomerName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.CustomerEmail)
            .EmailAddress()
            .MaximumLength(320)
            .When(x => !string.IsNullOrWhiteSpace(x.CustomerEmail));

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Matches("^[A-Za-z]{3}$")
            .WithMessage("'Currency' must be a 3-letter ISO 4217 code, e.g. USD, EUR, INR.");

        RuleFor(x => x.DueDate)
            .GreaterThanOrEqualTo(x => x.IssueDate)
            .WithMessage("'DueDate' cannot be earlier than 'IssueDate'.");

        RuleFor(x => x.TaxRate)
            .InclusiveBetween(0, 100)
            .WithMessage("'TaxRate' is a percentage and must be between 0 and 100.");

        RuleFor(x => x.Notes)
            .MaximumLength(2000);

        RuleFor(x => x.LineItems)
            .NotEmpty()
            .WithMessage("An invoice must contain at least one line item.");

        RuleFor(x => x.LineItems)
            .Must(items => items is null || items.Count <= MaxLineItems)
            .WithMessage($"An invoice cannot contain more than {MaxLineItems} line items.");

        RuleForEach(x => x.LineItems).SetValidator(new CreateInvoiceLineItemRequestValidator());
    }
}

public sealed class CreateInvoiceLineItemRequestValidator : AbstractValidator<CreateInvoiceLineItemRequest>
{
    public CreateInvoiceLineItemRequestValidator()
    {
        RuleFor(x => x.Description)
            .NotEmpty()
            .MaximumLength(500);

        RuleFor(x => x.Quantity)
            .GreaterThan(0)
            .LessThanOrEqualTo(1_000_000);

        RuleFor(x => x.UnitPrice)
            .GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(1_000_000_000);
    }
}

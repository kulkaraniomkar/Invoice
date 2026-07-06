using FluentValidation.TestHelper;
using Qwiik.Invoicing.Api.Contracts;
using Qwiik.Invoicing.Api.Validation;
using Xunit;

namespace Qwiik.Invoicing.Tests;

public class CreateInvoiceRequestValidatorTests
{
    private readonly CreateInvoiceRequestValidator _validator = new();

    private static CreateInvoiceRequest ValidRequest() => new(
        CustomerName: "Acme Corp",
        CustomerEmail: "billing@acme.test",
        Currency: "USD",
        IssueDate: new DateOnly(2026, 7, 1),
        DueDate: new DateOnly(2026, 7, 31),
        TaxRate: 18m,
        Notes: null,
        LineItems: [new CreateInvoiceLineItemRequest("Consulting", 2, 100m)]);

    [Fact]
    public void Valid_request_passes()
    {
        _validator.TestValidate(ValidRequest()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Customer_name_is_required()
    {
        var request = ValidRequest() with { CustomerName = "  " };
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CustomerName);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("U$D")]
    [InlineData("")]
    public void Currency_must_be_three_letters(string currency)
    {
        var request = ValidRequest() with { Currency = currency };
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Fact]
    public void Invalid_email_is_rejected_but_missing_email_is_fine()
    {
        var invalidEmail = ValidRequest() with { CustomerEmail = "not-an-email" };
        _validator.TestValidate(invalidEmail).ShouldHaveValidationErrorFor(x => x.CustomerEmail);

        var noEmail = ValidRequest() with { CustomerEmail = null };
        _validator.TestValidate(noEmail).ShouldNotHaveValidationErrorFor(x => x.CustomerEmail);
    }

    [Fact]
    public void Due_date_before_issue_date_is_rejected()
    {
        var request = ValidRequest() with { DueDate = new DateOnly(2026, 6, 30) };
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.DueDate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Tax_rate_must_be_a_percentage(decimal taxRate)
    {
        var request = ValidRequest() with { TaxRate = taxRate };
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.TaxRate);
    }

    [Fact]
    public void At_least_one_line_item_is_required()
    {
        var request = ValidRequest() with { LineItems = [] };
        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.LineItems);
    }

    [Fact]
    public void Line_item_quantity_must_be_positive_and_price_non_negative()
    {
        var badQuantity = ValidRequest() with
        {
            LineItems = [new CreateInvoiceLineItemRequest("Service", 0, 10m)]
        };
        _validator.TestValidate(badQuantity)
            .ShouldHaveValidationErrorFor("LineItems[0].Quantity");

        var badPrice = ValidRequest() with
        {
            LineItems = [new CreateInvoiceLineItemRequest("Service", 1, -5m)]
        };
        _validator.TestValidate(badPrice)
            .ShouldHaveValidationErrorFor("LineItems[0].UnitPrice");
    }
}

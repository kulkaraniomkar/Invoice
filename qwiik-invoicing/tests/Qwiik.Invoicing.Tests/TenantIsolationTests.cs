using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Qwiik.Invoicing.Api.Infrastructure;
using Xunit;

namespace Qwiik.Invoicing.Tests;

/// <summary>
/// Verifies the two tenant-isolation mechanisms against a real relational database
/// (SQLite in-memory): the global query filter and TenantId stamping on save.
/// These are the highest-risk rules in a multi-tenant system, so they get
/// database-backed tests rather than mocks.
/// </summary>
public sealed class TenantIsolationTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly FakeTenantProvider _tenantProvider = new();

    public TenantIsolationTests()
    {
        // Keep one open connection so the in-memory database lives for the whole test.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _tenantProvider.TenantId = TenantA;
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    private InvoicingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InvoicingDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new InvoicingDbContext(options, _tenantProvider);
    }

    private async Task SeedInvoiceAsAsync(Guid tenantId, string invoiceNumber)
    {
        _tenantProvider.TenantId = tenantId;
        await using var context = CreateContext();
        context.Invoices.Add(TestData.NewInvoice(invoiceNumber: invoiceNumber));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Tenant_id_is_stamped_from_ambient_tenant_on_save()
    {
        await SeedInvoiceAsAsync(TenantA, "INV-A-001");

        await using var context = CreateContext();
        var saved = await context.Invoices.SingleAsync();

        Assert.Equal(TenantA, saved.TenantId);
    }

    [Fact]
    public async Task Queries_only_return_invoices_of_the_current_tenant()
    {
        await SeedInvoiceAsAsync(TenantA, "INV-A-001");
        await SeedInvoiceAsAsync(TenantA, "INV-A-002");
        await SeedInvoiceAsAsync(TenantB, "INV-B-001");

        _tenantProvider.TenantId = TenantA;
        await using (var contextA = CreateContext())
        {
            var visibleToA = await contextA.Invoices.ToListAsync();
            Assert.Equal(2, visibleToA.Count);
            Assert.All(visibleToA, i => Assert.Equal(TenantA, i.TenantId));
        }

        _tenantProvider.TenantId = TenantB;
        await using var contextB = CreateContext();
        var visibleToB = await contextB.Invoices.ToListAsync();
        Assert.Single(visibleToB);
        Assert.Equal("INV-B-001", visibleToB[0].InvoiceNumber);
    }

    [Fact]
    public async Task Fetching_another_tenants_invoice_by_id_returns_nothing()
    {
        await SeedInvoiceAsAsync(TenantA, "INV-A-001");

        Guid invoiceId;
        _tenantProvider.TenantId = TenantA;
        await using (var contextA = CreateContext())
            invoiceId = (await contextA.Invoices.SingleAsync()).Id;

        // Tenant B probes tenant A's invoice id — the filter must hide it,
        // which the API surfaces as a 404 (indistinguishable from "does not exist").
        _tenantProvider.TenantId = TenantB;
        await using var contextB = CreateContext();
        var result = await contextB.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId);

        Assert.Null(result);
    }

    [Fact]
    public async Task Same_invoice_number_is_allowed_across_tenants_but_not_within_one()
    {
        await SeedInvoiceAsAsync(TenantA, "INV-SHARED-001");

        // Different tenant, same number: fine.
        await SeedInvoiceAsAsync(TenantB, "INV-SHARED-001");

        // Same tenant, same number: unique index must reject it.
        await Assert.ThrowsAsync<DbUpdateException>(() => SeedInvoiceAsAsync(TenantA, "INV-SHARED-001"));
    }

    [Fact]
    public async Task Saving_without_an_ambient_tenant_is_rejected()
    {
        _tenantProvider.TenantId = Guid.Empty;
        await using var context = CreateContext();
        context.Invoices.Add(TestData.NewInvoice());

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    public void Dispose() => _connection.Dispose();
}

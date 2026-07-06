using Microsoft.EntityFrameworkCore;
using Qwiik.Invoicing.Api.Domain;
using Qwiik.Invoicing.Api.Infrastructure.Tenancy;

namespace Qwiik.Invoicing.Api.Infrastructure;

/// <summary>
/// Tenant-aware DbContext. Two mechanisms enforce isolation for every query and write:
///  1. A global query filter on TenantId — no query can "forget" the tenant predicate.
///  2. SaveChanges stamps TenantId from the ambient tenant — clients can never choose it.
/// </summary>
public class InvoicingDbContext(DbContextOptions<InvoicingDbContext> options, ITenantProvider tenantProvider)
    : DbContext(options)
{
    private readonly ITenantProvider _tenantProvider = tenantProvider;

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(builder =>
        {
            builder.ToTable("Invoices");
            builder.HasKey(i => i.Id);

            builder.Property(i => i.InvoiceNumber).HasMaxLength(30).IsRequired();
            builder.Property(i => i.CustomerName).HasMaxLength(200).IsRequired();
            builder.Property(i => i.CustomerEmail).HasMaxLength(320);
            builder.Property(i => i.Currency).HasMaxLength(3).IsRequired();
            builder.Property(i => i.Notes).HasMaxLength(2000);

            // Stored as string: self-documenting in the database and safe against
            // enum reordering. The (TenantId, Status) index keeps filtering cheap.
            builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            builder.Property(i => i.TaxRate).HasPrecision(5, 2);
            builder.Property(i => i.Subtotal).HasPrecision(18, 2);
            builder.Property(i => i.TaxAmount).HasPrecision(18, 2);
            builder.Property(i => i.Total).HasPrecision(18, 2);

            builder.Property(i => i.ConcurrencyToken).IsConcurrencyToken();

            // Every index leads with TenantId: all queries are tenant-scoped, so this
            // is what makes them seek instead of scan across tenants.
            builder.HasIndex(i => new { i.TenantId, i.InvoiceNumber }).IsUnique();
            builder.HasIndex(i => new { i.TenantId, i.Status });
            builder.HasIndex(i => new { i.TenantId, i.IssueDate });
            builder.HasIndex(i => new { i.TenantId, i.DueDate });
            builder.HasIndex(i => new { i.TenantId, i.CreatedAtUtc });

            // Applied automatically to every query against Invoices.
            builder.HasQueryFilter(i => i.TenantId == _tenantProvider.TenantId);

            builder.OwnsMany(i => i.LineItems, li =>
            {
                li.ToTable("InvoiceLineItems");
                li.WithOwner().HasForeignKey("InvoiceId");
                li.HasKey(x => x.Id);
                li.Property(x => x.Description).HasMaxLength(500).IsRequired();
                li.Property(x => x.Quantity).HasPrecision(18, 3);
                li.Property(x => x.UnitPrice).HasPrecision(18, 2);
                li.Property(x => x.LineTotal).HasPrecision(18, 2);
                li.HasIndex("InvoiceId");
            });

            // Line items live in a private backing field so the aggregate stays encapsulated.
            builder.Metadata.FindNavigation(nameof(Invoice.LineItems))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAuditAndTenantStamping();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAuditAndTenantStamping();
        return base.SaveChanges();
    }

    private void ApplyAuditAndTenantStamping()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Invoice>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (!_tenantProvider.HasTenant)
                        throw new InvalidOperationException(
                            "Cannot save an invoice without an ambient tenant. This indicates a bug in tenant resolution.");
                    entry.Entity.TenantId = _tenantProvider.TenantId;
                    entry.Entity.CreatedAtUtc = utcNow;
                    entry.Entity.UpdatedAtUtc = utcNow;
                    entry.Entity.ConcurrencyToken = Guid.NewGuid();
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = utcNow;
                    // Rotate the token; EF uses the *original* value in the WHERE clause,
                    // so a concurrent writer gets DbUpdateConcurrencyException (HTTP 409).
                    entry.Entity.ConcurrencyToken = Guid.NewGuid();
                    break;
            }
        }
    }
}

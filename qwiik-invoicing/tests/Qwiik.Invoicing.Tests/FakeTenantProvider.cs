using Qwiik.Invoicing.Api.Infrastructure.Tenancy;

namespace Qwiik.Invoicing.Tests;

/// <summary>Mutable tenant provider so tests can act as different tenants against the same database.</summary>
public sealed class FakeTenantProvider : ITenantProvider
{
    public Guid TenantId { get; set; }
    public bool HasTenant => TenantId != Guid.Empty;
}

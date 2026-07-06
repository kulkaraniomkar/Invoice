namespace Qwiik.Invoicing.Api.Infrastructure.Tenancy;

/// <summary>Scoped holder set once per request by <see cref="TenantResolutionMiddleware"/>.</summary>
public sealed class TenantProvider : ITenantProvider
{
    public Guid TenantId { get; private set; }
    public bool HasTenant => TenantId != Guid.Empty;

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        TenantId = tenantId;
    }
}

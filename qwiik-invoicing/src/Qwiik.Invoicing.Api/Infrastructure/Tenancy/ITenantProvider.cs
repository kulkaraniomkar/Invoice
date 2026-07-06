namespace Qwiik.Invoicing.Api.Infrastructure.Tenancy;

/// <summary>
/// Ambient tenant for the current request scope. The DbContext depends on this
/// abstraction (not on HttpContext), which keeps tenancy testable and would let
/// tenant resolution move to JWT claims without touching data access code.
/// </summary>
public interface ITenantProvider
{
    Guid TenantId { get; }
    bool HasTenant { get; }
}

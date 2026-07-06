using Microsoft.AspNetCore.Mvc;

namespace Qwiik.Invoicing.Api.Infrastructure.Tenancy;

/// <summary>
/// Resolves the tenant for every /api request from the X-Tenant-Id header and
/// rejects requests without a valid tenant before they reach any endpoint.
///
/// NOTE: a header is used to keep the assessment runnable without an identity
/// provider. In production the tenant id would come from a validated JWT claim
/// (see SOLUTION_NOTES.md → Tenant isolation), and this middleware would read
/// the claim instead — nothing downstream would change.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public const string TenantHeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext context, TenantProvider tenantProvider)
    {
        // Only API endpoints are tenant-scoped; /health and /swagger are not.
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(TenantHeaderName, out var headerValue)
            || !Guid.TryParse(headerValue.ToString(), out var tenantId)
            || tenantId == Guid.Empty)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing or invalid tenant.",
                Detail = $"A valid '{TenantHeaderName}' header containing a non-empty GUID is required."
            });
            return;
        }

        tenantProvider.SetTenant(tenantId);
        await next(context);
    }
}

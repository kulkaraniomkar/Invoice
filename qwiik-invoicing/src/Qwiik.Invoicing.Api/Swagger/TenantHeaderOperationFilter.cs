using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qwiik.Invoicing.Api.Swagger;

/// <summary>Documents the required X-Tenant-Id header on every operation in Swagger UI.</summary>
public sealed class TenantHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-Tenant-Id",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Tenant identifier (GUID). Stand-in for the tenant claim of a validated JWT in production.",
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" }
        });
    }
}

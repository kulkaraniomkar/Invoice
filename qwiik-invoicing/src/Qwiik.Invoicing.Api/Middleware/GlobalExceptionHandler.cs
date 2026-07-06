using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Qwiik.Invoicing.Api.Domain;

namespace Qwiik.Invoicing.Api.Middleware;

/// <summary>
/// Single place where exceptions become RFC 7807 problem responses:
///   NotFoundException            → 404
///   DomainException              → 422 (request was well-formed but violates a business rule)
///   DbUpdateConcurrencyException → 409 (someone else modified the invoice; retry)
///   anything else                → 500 with a generic message — internals are logged, never leaked.
/// </summary>
internal sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title, detail) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Resource not found.", exception.Message),
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Business rule violation.", exception.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrency conflict.",
                "The invoice was modified by another request. Reload it and try again."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.",
                "The error has been logged. Please try again or contact support.")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        else
            logger.LogWarning("Request {Method} {Path} rejected with {StatusCode}: {Message}",
                httpContext.Request.Method, httpContext.Request.Path, statusCode, exception.Message);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        }, cancellationToken);

        return true;
    }
}

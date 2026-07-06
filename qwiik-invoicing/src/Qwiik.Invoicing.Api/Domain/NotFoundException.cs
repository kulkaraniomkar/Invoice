namespace Qwiik.Invoicing.Api.Domain;

/// <summary>
/// Requested resource does not exist (or belongs to another tenant — deliberately
/// indistinguishable, to avoid leaking cross-tenant information). Mapped to HTTP 404.
/// </summary>
public class NotFoundException(string message) : Exception(message);

namespace Qwiik.Invoicing.Api.Domain;

/// <summary>
/// A violated business rule. Mapped to HTTP 422 by the global exception handler.
/// </summary>
public class DomainException(string message) : Exception(message);

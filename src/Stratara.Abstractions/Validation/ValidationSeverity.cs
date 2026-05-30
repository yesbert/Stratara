namespace Stratara.Abstractions.Validation;

/// <summary>
/// Severity of a single <see cref="ValidationFailure"/>. Only <see cref="Error"/> blocks the
/// pipeline; <see cref="Warning"/> and <see cref="Info"/> pass through and are logged.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>The failure blocks the request — the mediator pipeline throws and the handler never runs.</summary>
    Error,

    /// <summary>A non-blocking advisory; the request still reaches the handler. Logged for the operator.</summary>
    Warning,

    /// <summary>A purely informational note; the request still reaches the handler. Logged for the operator.</summary>
    Info
}

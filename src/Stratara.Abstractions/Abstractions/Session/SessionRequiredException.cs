namespace Stratara.Abstractions.Session;

/// <summary>
/// Thrown when an operation that must be attributed to a caller — saving events, recording a command
/// audit, dispatching a command — runs with no session context set.
/// </summary>
/// <remarks>
/// <para>
/// The session middleware sets no session for an unauthenticated request, so on an HTTP host this
/// failure means the caller carries no identity. The opt-in problem-details mapping answers it with
/// <c>401</c> for a caller that is not authenticated; for an authenticated caller it is left
/// unconverted, because there it means the host did not establish the session context.
/// </para>
/// <para>
/// Derives from <see cref="InvalidOperationException"/>, so a handler that catches that type keeps
/// catching this one. Lives in <c>Stratara.Abstractions</c> so a host can catch it without
/// referencing any store or broker package.
/// </para>
/// </remarks>
public sealed class SessionRequiredException : InvalidOperationException
{
    /// <summary>Initializes the exception with a default message.</summary>
    public SessionRequiredException()
        : base("Session context is not set")
    {
    }

    /// <summary>Initializes the exception with the specified message.</summary>
    /// <param name="message">A human-readable description of the operation that needed a session.</param>
    public SessionRequiredException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with the specified message and the failure that caused it.</summary>
    /// <param name="message">A human-readable description of the operation that needed a session.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public SessionRequiredException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

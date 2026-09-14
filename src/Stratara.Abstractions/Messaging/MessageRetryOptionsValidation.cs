namespace Stratara.Abstractions.Messaging;

/// <summary>
/// The validation both transport registrations apply to <see cref="MessageRetryOptions"/> at
/// start-up: each bound is at least one, because a bound of zero would dead-letter a message
/// before its handler ever ran.
/// </summary>
public static class MessageRetryOptionsValidation
{
    /// <summary>The failure message reported when a bound is below one.</summary>
    public const string Message = "MessageRetry: MaxDeliveryAttempts and MaxConflictRequeues must each be at least 1.";

    /// <summary>Returns whether both bounds are at least one.</summary>
    /// <param name="options">The options to validate.</param>
    /// <returns><see langword="true"/> when both bounds are at least one.</returns>
    public static bool IsValid(MessageRetryOptions options) =>
        options.MaxDeliveryAttempts >= 1 && options.MaxConflictRequeues >= 1;
}

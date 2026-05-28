namespace Stratara.Abstractions.Mediator;

/// <summary>
/// Marker interface for an <see cref="IMediator"/> decorator that enforces
/// <c>[RequireRole]</c> authorization before dispatching to handlers. The startup-time
/// authorization validator uses this marker to recognise the authorizing dispatch path
/// at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The default authorizing decorator (registered via
/// <c>services.AddAuthorizingMediator&lt;TAuthorizationProvider&gt;()</c>) implements this
/// marker. If you wrap that decorator with a custom mediator implementation, your
/// outermost decorator must also implement <see cref="IAuthorizingMediator"/> so the
/// startup validator does not flag the host as missing role enforcement.
/// </para>
/// <para>
/// Implementing this marker is a contract: the implementer commits to evaluating any
/// <c>[RequireRole]</c> attributes on the request type before invoking the handler chain.
/// Implementing the marker without performing that check defeats the validator.
/// </para>
/// </remarks>
public interface IAuthorizingMediator : IMediator
{
}

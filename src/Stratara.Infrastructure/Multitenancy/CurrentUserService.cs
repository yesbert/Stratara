using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;

namespace Stratara.Infrastructure.Multitenancy;

/// <summary>
/// <see cref="ICurrentUserService"/> implementation that returns the <c>ActorUserId</c> from the
/// ambient <see cref="ISessionContextProvider"/> — i.e. the user who triggered the request
/// (audit identity), not the data-owner subject.
/// </summary>
internal sealed class CurrentUserService(ISessionContextProvider provider) : ICurrentUserService
{
    /// <inheritdoc/>
    public Guid GetId() => provider.Current?.ActorUserId ?? Guid.Empty;
}

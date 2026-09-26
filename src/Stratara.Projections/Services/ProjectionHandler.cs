using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Projections.Abstractions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Domain;
using Stratara.Shared.Diagnostics.Extensions;
using Stratara.Shared.Reflections;

namespace Stratara.Projections.Services;

/// <summary>
/// Default <see cref="IProjectionHandler"/> that drives a single projection over a list of events,
/// using the cached delegates exposed by <see cref="IProjectionMethodInvoker"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each event is first matched against a <c>HandleAsync(TEventPayload, CancellationToken)</c> overload;
/// if none exists, the handler falls back to a <c>HandleAsync(IEvent&lt;TEventPayload&gt;, CancellationToken)</c>
/// overload so handlers that need event metadata (stream id, version, …) can opt in.
/// </para>
/// <para>
/// For a projection that declares <see cref="IForgetsDeletedTenants"/>, <see cref="TenantDeleted"/> and
/// <see cref="CustomerTenantsDeleted"/> count as relevant whether or not it handles them. After one of
/// them has been applied, the tenants it deleted are recorded for the projection before the next event,
/// and a <see cref="PrecedingFactMissingException"/> for a fact owned by a recorded tenant is logged and
/// passed over instead of propagating.
/// </para>
/// </remarks>
internal sealed class ProjectionHandler(
    IProjectionMethodInvoker methodInvoker,
    ILogger<ProjectionHandler>? logger = null,
    IForgottenTenantStore? forgottenTenants = null) : IProjectionHandler
{
    private static readonly Type[] DeletionEventTypes = [typeof(TenantDeleted), typeof(CustomerTenantsDeleted)];

    /// <inheritdoc/>
    public Type[] GetRelevantEventTypes(IProjection projection)
    {
        var handled = methodInvoker.GetOrCreateRelevantEventTypes(projection);
        return projection is IForgetsDeletedTenants ? handled.Union(DeletionEventTypes).ToArray() : handled;
    }

    /// <inheritdoc/>
    public string[] GetRelevantEventTypeNames(IProjection projection) =>
        GetRelevantEventTypes(projection).Select(t => t.GetQualifiedTypeName()).ToArray();

    /// <inheritdoc/>
    public string GetProjectionName(IProjection projection) => projection.GetType().Name;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// The projection declares <see cref="IForgetsDeletedTenants"/> and no <see cref="IForgottenTenantStore"/> is registered.
    /// </exception>
    public async Task ProjectAsync(IProjection projection, IReadOnlyList<IEvent> events, CancellationToken cancellationToken = default)
    {
        if (projection is not IForgetsDeletedTenants)
        {
            foreach (var @event in events)
            {
                await HandleEventAsync(projection, @event, cancellationToken);
            }

            return;
        }

        var store = forgottenTenants ?? throw new InvalidOperationException(
            $"Projection '{GetProjectionName(projection)}' declares {nameof(IForgetsDeletedTenants)}, but no " +
            $"{nameof(IForgottenTenantStore)} is registered to keep the tenants it has seen deleted. Register the read store " +
            "with AddNpgsqlReadDbContextFactory<TContext>(), or call AddStrataraForgottenTenants<TReadContext>() for a read " +
            "context registered another way.");
        var projectionName = GetProjectionName(projection);
        foreach (var @event in events)
        {
            await HandleForgettingAsync(projection, projectionName, store, @event, cancellationToken);
        }
    }

    private async Task HandleForgettingAsync(IProjection projection, string projectionName, IForgottenTenantStore store, IEvent @event,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleEventAsync(projection, @event, cancellationToken);
        }
        catch (PrecedingFactMissingException)
        {
            if (!await store.HasForgottenAsync(projectionName, @event.TenantId, cancellationToken))
            {
                throw;
            }

            (logger ?? NullLogger<ProjectionHandler>.Instance).LogProjectionForgottenTenantFactPassedOver(
                projectionName, @event.StreamId, @event.EventTypeName, @event.TenantId);
            return;
        }

        var deleted = @event.Data switch
        {
            TenantDeleted => [@event.StreamId],
            CustomerTenantsDeleted cascade => cascade.TenantIds ?? [],
            _ => (IReadOnlyCollection<Guid>)[]
        };

        if (deleted.Count > 0)
        {
            await store.ForgetAsync(projectionName, deleted, cancellationToken);
        }
    }

    private async Task HandleEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        if (await TryHandleEventAsync(projection, @event, cancellationToken))
        {
            return;
        }

        await TryHandleWrappedEventAsync(projection, @event, cancellationToken);
    }

    private async Task<bool> TryHandleEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var handleDelegate = methodInvoker.GetOrCreateDelegate(projection, eventDataType);
        if (methodInvoker.IsNoOp(handleDelegate))
        {
            return false;
        }

        await handleDelegate(projection, @event.Data, cancellationToken);
        return true;
    }

    private Task TryHandleWrappedEventAsync(IProjection projection, IEvent @event, CancellationToken cancellationToken)
    {
        var eventDataType = @event.Data.GetType();
        var eventInterfaceType = typeof(IEvent<>).MakeGenericType(eventDataType);
        var handleDelegate = methodInvoker.GetOrCreateDelegate(projection, eventInterfaceType);
        return handleDelegate(projection, @event, cancellationToken);
    }
}

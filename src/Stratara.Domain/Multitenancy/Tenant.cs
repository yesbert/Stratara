using JetBrains.Annotations;
using Stratara.Abstractions.Domain;

namespace Stratara.Domain.Multitenancy;

/// <summary>
/// The Stratara framework's built-in tenant aggregate — the top-level isolation unit for
/// multitenant applications. Created on tenant onboarding, mutated by the lifecycle events
/// in <see cref="TenantCreated"/>, <see cref="TenantRenamed"/>, <see cref="TenantActivated"/>,
/// <see cref="TenantDeactivated"/>, <see cref="TenantDefaultLocaleChanged"/>,
/// <see cref="TenantAssignedToCustomer"/>, and <see cref="TenantDeleted"/>.
/// </summary>
/// <remarks>
/// Public setters are intentional — the Stratara snapshot deserializer needs them. State
/// transitions go through <c>Apply(...)</c> methods so that event-sourced rebuild and
/// snapshot-hydration both result in the same shape.
/// </remarks>
public sealed class Tenant : IAggregate
{
    /// <summary>The tenant's stable identifier (stream id for the event-sourced aggregate).</summary>
    public Guid Id { get; set; }

    /// <summary>Identifier of the customer that owns this tenant.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>Human-readable tenant name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// IETF BCP 47 language tag used as the tenant's default UI locale (e.g. <c>en-US</c>).
    /// Defaults to <see cref="string.Empty"/> so the framework stays culture-neutral; the
    /// consumer supplies the actual value through <see cref="TenantCreated.DefaultLocale"/>.
    /// </summary>
    public string DefaultLocale { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> while the tenant is operational; <c>false</c> when deactivated via
    /// <see cref="TenantDeactivated"/>. Deletion (<see cref="DeletedAt"/>) is a separate axis.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Timestamp the tenant aggregate was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Timestamp of the most recent mutating event, or <c>null</c> if untouched since creation.</summary>
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>Timestamp at which the tenant was soft-deleted, or <c>null</c> while active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary><c>true</c> when <see cref="DeletedAt"/> has a value.</summary>
    public bool IsDeleted => DeletedAt.HasValue;

    /// <summary>Apply method invoked by the event-source replay when a <see cref="TenantCreated"/> event is encountered.</summary>
    /// <param name="event">The event that opened the stream.</param>
    [UsedImplicitly]
    public void Apply(TenantCreated @event)
    {
        Id = @event.Id;
        CustomerId = @event.CustomerId;
        Name = @event.Name;
        DefaultLocale = @event.DefaultLocale;
        IsActive = @event.IsActive;
        CreatedAt = @event.CreatedAt;
    }

    /// <summary>Apply method for the soft-delete transition. Sets <see cref="DeletedAt"/>.</summary>
    /// <param name="event">The delete event.</param>
    [UsedImplicitly]
    public void Apply(TenantDeleted @event)
    {
        DeletedAt = @event.DeletedAt;
    }

    /// <summary>Apply method for renaming. Updates <see cref="Name"/> and <see cref="LastModifiedAt"/>.</summary>
    /// <param name="event">The rename event.</param>
    [UsedImplicitly]
    public void Apply(TenantRenamed @event)
    {
        Name = @event.Name;
        LastModifiedAt = @event.ChangedAt;
    }

    /// <summary>Apply method that flips <see cref="IsActive"/> back to <c>true</c>.</summary>
    /// <param name="event">The activation event.</param>
    [UsedImplicitly]
    public void Apply(TenantActivated @event)
    {
        IsActive = true;
        LastModifiedAt = @event.ChangedAt;
    }

    /// <summary>Apply method that flips <see cref="IsActive"/> to <c>false</c>.</summary>
    /// <param name="event">The deactivation event.</param>
    [UsedImplicitly]
    public void Apply(TenantDeactivated @event)
    {
        IsActive = false;
        LastModifiedAt = @event.ChangedAt;
    }

    /// <summary>Apply method that swaps the <see cref="DefaultLocale"/>.</summary>
    /// <param name="event">The locale-change event.</param>
    [UsedImplicitly]
    public void Apply(TenantDefaultLocaleChanged @event)
    {
        DefaultLocale = @event.DefaultLocale;
        LastModifiedAt = @event.ChangedAt;
    }

    /// <summary>Apply method that re-parents the tenant to a different customer.</summary>
    /// <param name="event">The customer-reassignment event.</param>
    [UsedImplicitly]
    public void Apply(TenantAssignedToCustomer @event)
    {
        CustomerId = @event.CustomerId;
        LastModifiedAt = @event.ChangedAt;
    }
}

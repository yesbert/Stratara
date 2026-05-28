namespace Stratara.Abstractions.Entities;

/// <summary>
/// Marker for entities that carry a per-user identity (e.g. per-user
/// projection views: UserSettings, UserProfile, AssistantView). Distinct
/// from <c>EventStreamEntry</c>'s Actor/Subject UserId fields — those
/// are envelope columns, not entity-level identity.
/// </summary>
public interface IUserIdentity
{
    /// <summary>Identifier of the user that owns this entity.</summary>
    Guid UserId { get; set; }
}

namespace Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

/// <summary>
/// Marker for the DbContext that hosts the ASP.NET Core Identity tables
/// (<c>AspNetUsers</c>, <c>AspNetRoles</c>, <c>AspNetUserPasskeys</c>, …) so consumer hosts
/// can resolve the identity store separately from the write/read stores.
/// </summary>
public interface IIdentityDbContext : IDbContext;

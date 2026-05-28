using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.AspNetCore.Resources;

/// <summary>
/// Resource-anchor type for <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/> lookups in the
/// Stratara Identity AspNetCore stack. The companion <c>IdentityResources.resx</c> ships an English default
/// resource set; <c>IdentityResources.de.resx</c> provides German overrides. Consumers can ship additional
/// culture-specific resource files (e.g. <c>IdentityResources.fr.resx</c>) in their own assembly and register
/// a custom <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/> chain if more languages are needed.
/// </summary>
/// <remarks>
/// The class is intentionally empty — it exists only so that <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/>
/// can use its full type-name as the resource base-name. Do not add members.
/// </remarks>
[SuppressMessage(
    "Minor Code Smell",
    "S2094:Classes should not be empty",
    Justification = "IStringLocalizer<T> requires a type with an assembly-name-derived base name; the class must remain empty and cannot be an interface.")]
public sealed class IdentityResources
{
}

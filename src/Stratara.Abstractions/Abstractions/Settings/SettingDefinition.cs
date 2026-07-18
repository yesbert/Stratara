using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Settings;

/// <summary>
/// One entry of the application's setting vocabulary: a dotted name, its code-declared default,
/// and its behavioral flags. Definitions are declared once at startup in the
/// <see cref="SettingCatalog"/>; values are stored separately per <see cref="SettingScope"/>.
/// </summary>
/// <param name="Name">
/// The setting's unique dotted name (for example <c>"Notifications.Email.Enabled"</c>);
/// namespacing is purely conventional via the dots.
/// </param>
/// <param name="DefaultValue">
/// The value returned when no scope in the fallback chain (and no configuration entry) carries
/// one; <c>null</c> when the setting has no default.
/// </param>
/// <param name="IsInherited">
/// <c>true</c> (default): reads fall back through the scope chain (user-in-tenant → user →
/// tenant → global). <c>false</c>: reads consult only the most specific scope for the current
/// session, then configuration/default — a narrower scope never inherits a broader scope's value.
/// </param>
/// <param name="IsEncrypted">
/// <c>true</c> to store the value AES-GCM-encrypted at rest (scope-bound associated data via the
/// security plane's blob encryptor); requires an <c>ISecureBlobEncryptor</c> registration.
/// </param>
[ExcludeFromCodeCoverage]
public sealed record SettingDefinition(
    string Name,
    string? DefaultValue = null,
    bool IsInherited = true,
    bool IsEncrypted = false);

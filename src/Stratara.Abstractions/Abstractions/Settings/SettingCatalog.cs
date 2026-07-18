namespace Stratara.Abstractions.Settings;

/// <summary>
/// The application's setting vocabulary — every <see cref="SettingDefinition"/> the application
/// knows, declared in code at startup and registered as a singleton (the settings-plane sibling
/// of the authorization plane's <c>PermissionCatalog</c>).
/// </summary>
/// <remarks>
/// Declaration is strict: reading or writing an undeclared setting name through the framework
/// components throws, so typos surface immediately instead of silently returning nothing.
/// Build the catalog completely during service registration and treat it as immutable afterwards.
/// </remarks>
/// <example>
/// <code>
/// services.AddSettingCatalog(catalog =>
/// {
///     catalog.Add(new SettingDefinition("Notifications.Email.Enabled", "true"));
///     catalog.Add(new SettingDefinition("Ui.Theme", "system", IsInherited: false));
///     catalog.Add(new SettingDefinition("Smtp.Password", IsEncrypted: true));
/// });
/// </code>
/// </example>
public sealed class SettingCatalog
{
    private readonly Dictionary<string, SettingDefinition> _definitions = new(StringComparer.Ordinal);

    /// <summary>Every declared definition.</summary>
    public IReadOnlyCollection<SettingDefinition> All => _definitions.Values;

    /// <summary>
    /// Declares one or more settings.
    /// </summary>
    /// <param name="definitions">The definitions to declare.</param>
    /// <exception cref="ArgumentException">
    /// A definition's name is empty, or a name is declared twice (conflicting redefinition).
    /// </exception>
    public void Add(params SettingDefinition[] definitions)
    {
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new ArgumentException("Setting names must be non-empty.", nameof(definitions));
            }

            if (!_definitions.TryAdd(definition.Name, definition))
            {
                throw new ArgumentException(
                    $"Setting '{definition.Name}' is already declared; each name may be declared once.",
                    nameof(definitions));
            }
        }
    }

    /// <summary>
    /// Gets a declared definition, or <c>null</c> when the name is unknown.
    /// </summary>
    /// <param name="name">The setting name to look up.</param>
    /// <returns>The definition, or <c>null</c>.</returns>
    public SettingDefinition? GetOrNull(string name) => _definitions.GetValueOrDefault(name);

    /// <summary>
    /// Checks whether the setting name is declared.
    /// </summary>
    /// <param name="name">The setting name to check.</param>
    /// <returns><c>true</c> when declared.</returns>
    public bool Contains(string name) => _definitions.ContainsKey(name);
}

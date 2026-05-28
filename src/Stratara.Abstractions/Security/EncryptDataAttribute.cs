using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Security;

/// <summary>
/// Marks a property, class, or parameter for transparent encryption by
/// <see cref="Stratara.Abstractions.Security.ISecureJsonSerializer"/>. The
/// serializer pulls the matching DEK from <see cref="Stratara.Abstractions.Security.IKeyStore"/>
/// using the supplied <see cref="Level"/> + ambient Subject identifiers.
/// </summary>
/// <param name="level">The sensitivity tier — defaults to <see cref="DataSensitivityLevel.UserScoped"/>.</param>
/// <example>
/// Per-property encryption: only <c>Pan</c> is wrapped in the ciphertext envelope at rest and on the
/// command bus; other fields stay plaintext.
/// <code>
/// public sealed record AddPaymentCard(
///     Guid CustomerId,
///     [EncryptData(DataSensitivityLevel.UserScoped)] string Pan,
///     int ExpiryMonth,
///     int ExpiryYear) : ICommand;
/// </code>
/// </example>
[ExcludeFromCodeCoverage]
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Parameter)]
public sealed class EncryptDataAttribute(DataSensitivityLevel level = DataSensitivityLevel.UserScoped) : Attribute
{
    /// <summary>The configured sensitivity tier.</summary>
    public DataSensitivityLevel Level { get; } = level;
}

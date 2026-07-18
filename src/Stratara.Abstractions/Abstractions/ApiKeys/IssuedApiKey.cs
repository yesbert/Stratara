using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.ApiKeys;

/// <summary>
/// The one-time issuance result: the raw secret plus the stored descriptor. The raw key is
/// shown exactly once here and never persisted — display it to the caller immediately; it
/// cannot be recovered afterwards.
/// </summary>
/// <param name="RawKey">The plaintext API key (secret; hand to the caller once).</param>
/// <param name="Descriptor">The stored, non-secret description of the key.</param>
[ExcludeFromCodeCoverage]
public sealed record IssuedApiKey(string RawKey, ApiKeyDescriptor Descriptor);

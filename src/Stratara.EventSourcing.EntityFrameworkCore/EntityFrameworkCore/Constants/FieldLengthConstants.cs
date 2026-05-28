using System.Diagnostics.CodeAnalysis;

namespace Stratara.EventSourcing.EntityFrameworkCore.Constants;

/// <summary>
/// Shared maximum-length defaults applied by <c>ApplyDefaultStringLengths</c> to common
/// string columns (<c>Name</c>, <c>Description</c>, <c>Email</c>, …) across all Stratara
/// entity configurations.
/// </summary>
[ExcludeFromCodeCoverage]
public static class FieldLengthConstants
{
    /// <summary>Maximum length of a short machine-friendly code (e.g. country code).</summary>
    public const int Code = 64;

    /// <summary>Maximum length of a URL-safe slug.</summary>
    public const int Slug = 64;

    /// <summary>Maximum length of a human display name.</summary>
    public const int Name = 128;

    /// <summary>Maximum length of a UI label.</summary>
    public const int Label = 255;

    /// <summary>Maximum length of a free-text description.</summary>
    public const int Description = 4000;

    /// <summary>Maximum length of an e-mail address (RFC 5321 upper bound).</summary>
    public const int Email = 320;

    /// <summary>Maximum length of a phone number including extension and formatting.</summary>
    public const int Phone = 40;

    /// <summary>Maximum length of a URL.</summary>
    public const int Url = 2048;
}

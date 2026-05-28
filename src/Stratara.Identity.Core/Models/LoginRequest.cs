using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.Core.Models;

/// <summary>Payload sent by the client to perform a password sign-in against the identity endpoint.</summary>
[ExcludeFromCodeCoverage]
public sealed class LoginRequest
{
    /// <summary>The user's email address. Required and validated as an email format.</summary>
    [Required]
    [Display(Name = "Email Address")]
    [EmailAddress]
    public string Email { get; set; } = "";

    /// <summary>The user's password in clear text (transmitted over TLS).</summary>
    [Required]
    [Display(Name = "Password")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";
}

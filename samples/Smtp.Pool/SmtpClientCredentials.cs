using System.ComponentModel.DataAnnotations;

namespace Smtp.Pool;

/// <summary>
/// Credentials used to authenticate each pooled SMTP client against the host.
/// </summary>
/// <remarks>In production these should come from a secret store, not configuration files. See the completion report.</remarks>
public sealed class SmtpClientCredentials
{
    /// <summary>
    /// The user name (often the full email address) used to authenticate.
    /// </summary>
    [Required]
    public string UserName { get; set; } = null!;

    /// <summary>
    /// The password or application-specific token used to authenticate.
    /// </summary>
    [Required]
    public string Password { get; set; } = null!;
}

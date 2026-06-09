using System.ComponentModel.DataAnnotations;

namespace Smtp.Pool;

/// <summary>
/// Connection settings for the SMTP host the pooled clients connect to.
/// </summary>
public sealed class SmtpHostOptions
{
    /// <summary>
    /// The SMTP server host name or IP address.
    /// </summary>
    [Required]
    public string Host { get; set; } = null!;

    /// <summary>
    /// The SMTP server port. Defaults to 25.
    /// </summary>
    /// <remarks>Common values: 25 (plain/relay), 465 (implicit TLS), 587 (STARTTLS submission).</remarks>
    [Range(1, 65535)]
    public int Port { get; set; } = 25;

    /// <summary>
    /// When true, connect using implicit SSL/TLS (typically port 465).
    /// </summary>
    public bool UseSsl { get; set; }
}

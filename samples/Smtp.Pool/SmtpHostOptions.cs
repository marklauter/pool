using MailKit.Security;
using System.ComponentModel.DataAnnotations;

namespace Smtp.Pool;

/// <summary>
/// Connection settings for the SMTP host: where to reach the server and how to secure the transport.
/// </summary>
public sealed class SmtpHostOptions
{
    /// <summary>
    /// The SMTP server host name or IP address.
    /// </summary>
    [Required]
    public string Host { get; set; } = null!;

    /// <summary>
    /// The SMTP server port. Defaults to 587, the message-submission port.
    /// </summary>
    /// <remarks>587 pairs with <see cref="SecureSocketOptions.StartTls"/>, 465 with <see cref="SecureSocketOptions.SslOnConnect"/>, and 25 with <see cref="SecureSocketOptions.None"/> for an unsecured relay.</remarks>
    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// How the transport is secured. Defaults to <see cref="SecureSocketOptions.StartTls"/> (upgrade the plaintext connection to TLS, and fail if the server will not).
    /// </summary>
    /// <remarks>Prefer the explicit <see cref="SecureSocketOptions.StartTls"/> or <see cref="SecureSocketOptions.SslOnConnect"/> over <see cref="SecureSocketOptions.Auto"/> so a misconfigured server cannot silently downgrade to plaintext.</remarks>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    /// <summary>
    /// When true (the default), the server certificate must chain to a trusted root and match the host name.
    /// Set false only to accept self-signed certificates in development.
    /// </summary>
    public bool RequireValidCertificate { get; set; } = true;

    /// <summary>
    /// When true (the default), the server certificate is checked against revocation lists during the TLS handshake.
    /// </summary>
    /// <remarks>Disable only where an environment cannot reach the CA's revocation endpoints and you accept the reduced assurance.</remarks>
    public bool CheckCertificateRevocation { get; set; } = true;
}

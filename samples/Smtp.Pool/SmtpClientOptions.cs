namespace Smtp.Pool;

/// <summary>
/// Per-client settings applied when the factory constructs a new <see cref="MailKit.Net.Smtp.SmtpClient"/>.
/// </summary>
public sealed class SmtpClientOptions
{
    /// <summary>
    /// Socket-level timeout, in milliseconds, applied to each client. Defaults to 120000 (2 minutes), matching MailKit's default.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 120_000;
}

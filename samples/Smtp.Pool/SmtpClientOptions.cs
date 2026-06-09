namespace Smtp.Pool;

/// <summary>
/// Per-client behavior: the socket timeout and the connection-lifetime limits that real SMTP servers
/// enforce. A pooled connection is recycled (its transport reconnected) on the next lease once any one
/// of these limits is reached — whichever comes first — so the pool stays a step ahead of a server-side drop.
/// </summary>
/// <remarks>Set a lifetime to <see cref="System.TimeSpan.Zero"/> or a count to zero to disable that single limit. Match these to your provider's published limits (for example, Exchange-style: idle 10 minutes, total 20 minutes, 100 messages).</remarks>
public sealed class SmtpClientOptions
{
    /// <summary>
    /// Socket-level timeout, in milliseconds, applied to each client. Defaults to 120000 (2 minutes), matching MailKit's default.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = 120_000;

    /// <summary>
    /// Maximum total age of a connection before it is recycled, measured from when it connected. Defaults to 20 minutes.
    /// </summary>
    public TimeSpan MaxConnectionLifetime { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Maximum time a connection may sit idle (no send or NOOP) before it is recycled. Defaults to 10 minutes.
    /// </summary>
    public TimeSpan MaxIdleLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum number of messages sent over a single connection before it is recycled. Defaults to 100.
    /// </summary>
    public int MaxMessagesPerConnection { get; set; } = 100;

    /// <summary>
    /// A leased connection used more recently than this is handed out without a NOOP liveness probe;
    /// past it, the pool issues a NOOP to confirm the connection is still alive. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ProbeAfter { get; set; } = TimeSpan.FromSeconds(30);
}

using MailKit;
using MimeKit;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// A pooled SMTP connection: a stable pooled identity wrapped around a replaceable
/// <see cref="IMailTransport"/>. It tracks the per-connection limits real servers enforce — total age,
/// idle time, and messages sent — so the preparation strategy can recycle the underlying transport
/// before the server drops it. The pool leases the connection exclusively, so no internal locking is required.
/// </summary>
public sealed class SmtpConnection
    : IDisposable
{
    private readonly Func<IMailTransport> transportFactory;
    private readonly SmtpClientOptions options;
    private readonly TimeProvider timeProvider;
    private IMailTransport transport;
    private DateTimeOffset? connectedAt;
    private DateTimeOffset lastActivityAt;
    private int messageCount;

    internal SmtpConnection(
        Func<IMailTransport> transportFactory,
        SmtpClientOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.transportFactory = transportFactory;
        this.options = options;
        this.timeProvider = timeProvider;
        transport = transportFactory();
        lastActivityAt = timeProvider.GetUtcNow();
    }

    internal bool IsConnected => transport.IsConnected;

    internal bool IsAuthenticated => transport.IsAuthenticated;

    internal TimeSpan IdleFor(DateTimeOffset now) => now - lastActivityAt;

    /// <summary>
    /// True when any one of the configured lifetime limits has been reached — messages sent, total age,
    /// or idle time — whichever comes first. A connection that has not yet connected is never recyclable.
    /// </summary>
    internal bool ShouldRecycle(DateTimeOffset now) =>
        connectedAt is { } since
        && (options.MaxMessagesPerConnection > 0 && messageCount >= options.MaxMessagesPerConnection
            || options.MaxConnectionLifetime > TimeSpan.Zero && now - since >= options.MaxConnectionLifetime
            || options.MaxIdleLifetime > TimeSpan.Zero && now - lastActivityAt >= options.MaxIdleLifetime);

    /// <summary>
    /// Sends a message over the leased connection. Lease it with <c>LeaseScopeAsync</c> and send within
    /// the <c>using</c> so the connection returns to the pool on scope exit; the pool guarantees
    /// exclusive use for the lease's duration.
    /// </summary>
    public async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _ = await transport.SendAsync(message, cancellationToken);
        ++messageCount;
        lastActivityAt = timeProvider.GetUtcNow();
    }

    internal async Task ConnectAsync(SmtpHostOptions host, CancellationToken cancellationToken)
    {
        await transport.ConnectAsync(host.Host, host.Port, host.Security, cancellationToken);
        var now = timeProvider.GetUtcNow();
        connectedAt = now;
        lastActivityAt = now;
        messageCount = 0;
    }

    internal async Task AuthenticateAsync(SmtpClientCredentials credentials, CancellationToken cancellationToken)
    {
        await transport.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
        lastActivityAt = timeProvider.GetUtcNow();
    }

    internal Task DisconnectAsync(bool quit, CancellationToken cancellationToken) =>
        transport.DisconnectAsync(quit, cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A NOOP is a liveness probe: any failure means the connection is not ready and the pool should re-prepare it.")]
    internal async ValueTask<bool> PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await transport.NoOpAsync(cancellationToken);
        }
        catch
        {
            return false;
        }

        lastActivityAt = timeProvider.GetUtcNow();
        return true;
    }

    internal async Task RecycleAsync(CancellationToken cancellationToken)
    {
        await SafeQuitAsync(transport, cancellationToken);
        transport.Dispose();
        transport = transportFactory();
        connectedAt = null;
        messageCount = 0;
        lastActivityAt = timeProvider.GetUtcNow();
    }

    public void Dispose()
    {
        SafeQuit(transport);
        transport.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort graceful QUIT during recycle; the transport is being replaced, so failures are ignored.")]
    private static async Task SafeQuitAsync(IMailTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            if (transport.IsConnected)
            {
                await transport.DisconnectAsync(quit: true, cancellationToken);
            }
        }
        catch
        {
            // best effort
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort graceful QUIT during dispose; the pool is tearing the connection down, so failures are ignored.")]
    private static void SafeQuit(IMailTransport transport)
    {
        try
        {
            if (transport.IsConnected)
            {
                transport.Disconnect(quit: true, CancellationToken.None);
            }
        }
        catch
        {
            // best effort
        }
    }
}

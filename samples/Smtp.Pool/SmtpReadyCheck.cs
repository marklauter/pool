using Microsoft.Extensions.Options;
using Pool;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// Brings a pooled <see cref="SmtpConnection"/> to a ready state before each lease, and ages out
/// connections that have reached a lifetime limit: an idle-but-live connection is reused after a NOOP
/// liveness check, while an aged-out or dropped one is recycled (reconnected) by <see cref="PrepareAsync"/>.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the DI container via AddPreparationStrategy in SmtpClientPoolServiceCollectionExtensions.")]
internal sealed class SmtpReadyCheck(
    IOptions<SmtpHostOptions> hostOptions,
    IOptions<SmtpClientCredentials> credentials,
    IOptions<SmtpClientOptions> clientOptions,
    TimeProvider timeProvider)
    : IPreparationStrategy<SmtpConnection>
{
    private readonly SmtpHostOptions hostOptions = hostOptions?.Value
        ?? throw new ArgumentNullException(nameof(hostOptions));
    private readonly SmtpClientCredentials credentials = credentials?.Value
        ?? throw new ArgumentNullException(nameof(credentials));
    private readonly SmtpClientOptions clientOptions = clientOptions?.Value
        ?? throw new ArgumentNullException(nameof(clientOptions));
    private readonly TimeProvider timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public async ValueTask<bool> IsReadyAsync(SmtpConnection item, CancellationToken cancellationToken)
    {
        if (item is null || !item.IsConnected || !item.IsAuthenticated)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (item.ShouldRecycle(now))
        {
            return false; // aged out — PrepareAsync reconnects a fresh transport
        }

        if (item.IdleFor(now) < clientOptions.ProbeAfter)
        {
            return true; // used recently — trust it without a NOOP round-trip
        }

        return await item.PingAsync(cancellationToken);
    }

    public async Task PrepareAsync(SmtpConnection item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.ShouldRecycle(timeProvider.GetUtcNow()))
        {
            await item.RecycleAsync(cancellationToken); // fresh transport for an aged-out connection
        }
        else if (item.IsConnected)
        {
            await item.DisconnectAsync(quit: false, cancellationToken); // clean a half-open socket before reconnecting
        }

        await item.ConnectAsync(hostOptions, cancellationToken);
        await item.AuthenticateAsync(credentials, cancellationToken);
    }
}

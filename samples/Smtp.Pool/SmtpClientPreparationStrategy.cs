using MailKit;
using Microsoft.Extensions.Options;
using Pool;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// Brings a pooled <see cref="IMailTransport"/> to a ready state before it is leased: an idle client
/// is reused if it is still connected, authenticated, and responsive to NOOP; otherwise the pool
/// calls <see cref="PrepareAsync"/> to (re)connect and authenticate.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the DI container via AddPreparationStrategy in SmtpClientPoolServiceCollectionExtensions.")]
internal sealed class SmtpClientPreparationStrategy(
    IOptions<SmtpHostOptions> hostOptions,
    IOptions<SmtpClientCredentials> credentials)
    : IPreparationStrategy<IMailTransport>
{
    private readonly SmtpHostOptions hostOptions = hostOptions?.Value
        ?? throw new ArgumentNullException(nameof(hostOptions));
    private readonly SmtpClientCredentials credentials = credentials?.Value
        ?? throw new ArgumentNullException(nameof(credentials));

    public async ValueTask<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
        item is not null
        && item.IsConnected
        && item.IsAuthenticated
        && await NoOpAsync(item, cancellationToken);

    public async Task PrepareAsync(IMailTransport item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        await item.ConnectAsync(hostOptions.Host, hostOptions.Port, hostOptions.Security, cancellationToken);
        await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A NOOP is a liveness probe: any failure (timeout, dropped socket, protocol error) means the client is not ready, so the pool should re-prepare it.")]
    private static async Task<bool> NoOpAsync(IMailTransport item, CancellationToken cancellationToken)
    {
        try
        {
            await item.NoOpAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

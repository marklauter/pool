using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Pool;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// Creates pooled <see cref="SmtpConnection"/> instances. Construction is pure — no I/O — so the
/// expensive connect/authenticate work happens later in the preparation strategy. Each transport the
/// connection creates (initially and on recycle) carries the configured socket timeout and certificate policy.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the DI container via AddPoolItemFactory in SmtpClientPoolServiceCollectionExtensions.")]
internal sealed class SmtpConnectionFactory(
    IOptions<SmtpClientOptions> clientOptions,
    IOptions<SmtpHostOptions> hostOptions,
    TimeProvider timeProvider)
    : IItemFactory<SmtpConnection>
{
    private readonly SmtpClientOptions clientOptions = clientOptions?.Value
        ?? throw new ArgumentNullException(nameof(clientOptions));
    private readonly SmtpHostOptions hostOptions = hostOptions?.Value
        ?? throw new ArgumentNullException(nameof(hostOptions));
    private readonly TimeProvider timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public SmtpConnection CreateItem() => new(CreateTransport, clientOptions, timeProvider);

    // internal so the connection's recycle path and the unit tests can build a configured transport.
    internal IMailTransport CreateTransport()
    {
        var client = new SmtpClient
        {
            Timeout = clientOptions.TimeoutMilliseconds,
            CheckCertificateRevocation = hostOptions.CheckCertificateRevocation,
        };

        if (!hostOptions.RequireValidCertificate)
        {
            AcceptInvalidCertificates(client);
        }

        return client;
    }

    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "Opt-in, development-only acceptance of self-signed certificates, gated behind RequireValidCertificate = false.")]
    private static void AcceptInvalidCertificates(SmtpClient client) =>
        client.ServerCertificateValidationCallback = static (_, _, _, _) => true;
}

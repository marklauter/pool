using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Pool;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool;

/// <summary>
/// Creates the pooled <see cref="IMailTransport"/> instances and applies construction-time settings —
/// socket timeout and certificate policy. The pool owns each created client and disposes it when the
/// item is evicted or the pool is disposed.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by the DI container via AddPoolItemFactory in SmtpClientPoolServiceCollectionExtensions.")]
internal sealed class SmtpClientFactory(
    IOptions<SmtpClientOptions> clientOptions,
    IOptions<SmtpHostOptions> hostOptions)
    : IItemFactory<IMailTransport>
{
    private readonly SmtpClientOptions clientOptions = clientOptions?.Value
        ?? throw new ArgumentNullException(nameof(clientOptions));
    private readonly SmtpHostOptions hostOptions = hostOptions?.Value
        ?? throw new ArgumentNullException(nameof(hostOptions));

    public IMailTransport CreateItem()
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

    // Secure by default: only when the operator opts out of validation do we install an accept-all
    // callback. Gated and justified so the security analyzer stays on everywhere else.
    [SuppressMessage("Security", "CA5359:Do not disable certificate validation", Justification = "Opt-in, development-only acceptance of self-signed certificates, gated behind RequireValidCertificate = false.")]
    private static void AcceptInvalidCertificates(SmtpClient client) =>
        client.ServerCertificateValidationCallback = static (_, _, _, _) => true;
}

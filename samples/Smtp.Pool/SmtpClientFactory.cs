using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Pool;

namespace Smtp.Pool;

/// <summary>
/// Creates the pooled <see cref="IMailTransport"/> instances. The pool owns each created client
/// and disposes it when the item is evicted or the pool is disposed.
/// </summary>
public sealed class SmtpClientFactory(IOptions<SmtpClientOptions> clientOptions)
    : IItemFactory<IMailTransport>
{
    private readonly SmtpClientOptions clientOptions = clientOptions?.Value
        ?? throw new ArgumentNullException(nameof(clientOptions));

    public IMailTransport CreateItem()
    {
        var client = new SmtpClient
        {
            Timeout = clientOptions.TimeoutMilliseconds,
        };

        return client;
    }
}

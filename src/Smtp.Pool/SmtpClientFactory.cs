using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using Pool;

namespace Smtp.Pool;

internal sealed class SmtpClientFactory
    : IPoolItemFactory<IMailTransport>
{
    private readonly SmtpHostOptions hostOptions;
    private readonly SmtpClientOptions clientOptions;
    private readonly SmtpClientCredentials credentials;

    public SmtpClientFactory(
        IOptions<SmtpClientOptions> clientOptions,
        IOptions<SmtpHostOptions> hostOptions,
        IOptions<SmtpClientCredentials> credentials)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(credentials);

        this.clientOptions = clientOptions.Value;
        this.hostOptions = hostOptions.Value;
        this.credentials = credentials.Value;
    }

    public IMailTransport CreateItem() =>
        // todo: a real world example would set values from SmtpClientOptions
        new SmtpClient();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "it's part of the interface")]
    public async Task<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
            item.IsConnected
            && item.IsAuthenticated
            && await NoOpAsync(item, cancellationToken);

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

    public async Task MakeReadyAsync(IMailTransport item, CancellationToken cancellationToken)
    {
        await item.ConnectAsync(hostOptions.Host, hostOptions.Port, hostOptions.UseSsl, cancellationToken);
        await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
    }
}


using MailKit;
using Pool;

namespace Smtp.Pool;

internal sealed class SmtpClientPreparationStrategy(
    SmtpHostOptions hostOptions,
    SmtpClientCredentials credentials)
    : IPreparationStrategy<IMailTransport>
{
    public async ValueTask<bool> IsReadyAsync(IMailTransport item, CancellationToken cancellationToken) =>
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

    public async Task PrepareAsync(IMailTransport item, CancellationToken cancellationToken)
    {
        await item.ConnectAsync(hostOptions.Host, hostOptions.Port, hostOptions.UseSsl, cancellationToken);
        await item.AuthenticateAsync(credentials.UserName, credentials.Password, cancellationToken);
    }
}

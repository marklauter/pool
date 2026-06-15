using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Pool;
using Smtp.Pool.Integration.Tests.Fixtures;
using System.Globalization;

namespace Smtp.Pool.Integration.Tests;

[Collection("Smtp4devCollection")]
public sealed class SendAndReceiveTests(Smtp4devFixture smtp4dev)
{
    [Fact]
    public async Task Leased_Connection_Sends_A_Message_The_Server_Receives()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var subject = "integration-" + Guid.NewGuid().ToString("N");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SmtpHostOptions:Host"] = smtp4dev.Host,
                ["SmtpHostOptions:Port"] = smtp4dev.SmtpPort.ToString(CultureInfo.InvariantCulture),
                ["SmtpHostOptions:Security"] = "None",
                ["SmtpClientCredentials:UserName"] = "test",
                ["SmtpClientCredentials:Password"] = "test",
                ["PoolOptions:MaxSize"] = "4",
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        _ = services.AddSmtpClientPool(configuration);
        using var provider = services.BuildServiceProvider();
        var pool = provider.GetRequiredService<IPool<SmtpConnection>>();

        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("sender@example.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "hello from the pool" };

        // scoped lease: the connection returns to the pool when the using block exits, before we
        // switch over to checking the inbox
        using (var lease = await pool.LeaseScopeAsync(cancellationToken))
        {
            await lease.Item.SendAsync(message, cancellationToken);
        }

        using var received = await WaitForMessageAsync(subject, cancellationToken);

        Assert.Equal(subject, received.Subject);
        Assert.Equal("sender@example.test", received.From.Mailboxes.Single().Address);
        Assert.Equal("recipient@example.test", received.To.Mailboxes.Single().Address);
    }

    private async Task<MimeMessage> WaitForMessageAsync(string subject, CancellationToken cancellationToken)
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(smtp4dev.Host, smtp4dev.ImapPort, SecureSocketOptions.None, cancellationToken);
        await imap.AuthenticateAsync("test", "test", cancellationToken);
        _ = await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await imap.NoOpAsync(cancellationToken); // flush new-mail notifications into the open folder
            var uids = await imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject), cancellationToken);
            if (uids.Count > 0)
            {
                var message = await imap.Inbox.GetMessageAsync(uids[0], cancellationToken);
                await imap.DisconnectAsync(true, cancellationToken);
                return message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        await imap.DisconnectAsync(true, cancellationToken);
        throw new InvalidOperationException($"No message with subject '{subject}' arrived within the timeout.");
    }
}

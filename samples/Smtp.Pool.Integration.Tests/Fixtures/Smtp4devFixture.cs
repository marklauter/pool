using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Smtp.Pool.Integration.Tests.Fixtures;

/// <summary>
/// Starts a real smtp4dev SMTP/IMAP server in a container for the lifetime of the test collection.
/// Configured to require SMTP authentication and accept the test credentials, with a catch-all mailbox
/// the test user can read back over IMAP. Exposes the randomly-mapped host ports.
/// </summary>
public sealed class Smtp4devFixture : IAsyncLifetime
{
    private readonly IContainer container = new ContainerBuilder("rnwood/smtp4dev:v3")
        .WithEnvironment("ServerOptions__AuthenticationRequired", "true")
        .WithEnvironment("ServerOptions__Users__0__Username", "test")
        .WithEnvironment("ServerOptions__Users__0__Password", "test")
        .WithEnvironment("ServerOptions__Users__0__DefaultMailbox", "Default")
        .WithEnvironment("ServerOptions__Mailboxes__0__Name", "Default")
        .WithEnvironment("ServerOptions__Mailboxes__0__Recipients", "*")
        .WithEnvironment("ServerOptions__Urls", "http://*:80")
        .WithPortBinding(25, true)
        .WithPortBinding(143, true)
        .WithPortBinding(80, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(80)))
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    public string Host => container.Hostname;

    public ushort SmtpPort => container.GetMappedPublicPort(25);

    public ushort ImapPort => container.GetMappedPublicPort(143);

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

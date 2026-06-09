using MailKit;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Smtp.Pool.Tests;

public sealed class SmtpConnectionPreparationStrategyTests
{
    private static readonly SmtpHostOptions Host = new() { Host = "smtp.example.test", Port = 587 };
    private static readonly SmtpClientCredentials Credentials = new() { UserName = "user", Password = "pass" };

    private static SmtpConnectionPreparationStrategy CreateStrategy(SmtpClientOptions options, FakeTimeProvider clock) =>
        new(Options.Create(Host), Options.Create(Credentials), Options.Create(options), clock);

    private static SmtpConnection CreateConnection(SmtpClientOptions options, FakeTimeProvider clock, List<IMailTransport> created) =>
        new(() =>
        {
            var transport = Substitute.For<IMailTransport>();
            created.Add(transport);
            return transport;
        }, options, clock);

    [Fact]
    public async Task IsReadyAsync_True_When_Connected_Authenticated_And_Recently_Used()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        _ = created[0].IsAuthenticated.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);

        var ready = await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None);

        Assert.True(ready);
        await created[0].DidNotReceive().NoOpAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsReadyAsync_False_When_Not_Connected()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions();
        using var connection = CreateConnection(options, clock, []);

        Assert.False(await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task IsReadyAsync_False_When_Not_Authenticated()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        _ = created[0].IsAuthenticated.Returns(false);
        await connection.ConnectAsync(Host, CancellationToken.None);

        Assert.False(await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task IsReadyAsync_False_When_Aged_Out()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions { MaxIdleLifetime = TimeSpan.FromMinutes(1), MaxConnectionLifetime = TimeSpan.Zero, MaxMessagesPerConnection = 0 };
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        _ = created[0].IsAuthenticated.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.False(await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task IsReadyAsync_Pings_When_Idle_Beyond_ProbeAfter()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions { ProbeAfter = TimeSpan.FromSeconds(30), MaxIdleLifetime = TimeSpan.FromMinutes(10) };
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        _ = created[0].IsAuthenticated.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(1)); // beyond ProbeAfter, within idle limit

        Assert.True(await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None));
        await created[0].Received(1).NoOpAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsReadyAsync_False_When_Ping_Fails()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions { ProbeAfter = TimeSpan.FromSeconds(30), MaxIdleLifetime = TimeSpan.FromMinutes(10) };
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        _ = created[0].IsAuthenticated.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);
        _ = created[0].NoOpAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("dropped"));

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await CreateStrategy(options, clock).IsReadyAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareAsync_Connects_And_Authenticates_When_Fresh()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);

        await CreateStrategy(options, clock).PrepareAsync(connection, CancellationToken.None);

        await created[0].Received(1).ConnectAsync(Host.Host, Host.Port, Host.Security, Arg.Any<CancellationToken>());
        await created[0].Received(1).AuthenticateAsync(Credentials.UserName, Credentials.Password, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_Disconnects_Half_Open_Before_Reconnecting()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);

        await CreateStrategy(options, clock).PrepareAsync(connection, CancellationToken.None);

        await created[0].Received(1).DisconnectAsync(false, Arg.Any<CancellationToken>());
        _ = Assert.Single(created); // same transport reused, not recycled
    }

    [Fact]
    public async Task PrepareAsync_Recycles_An_Aged_Out_Connection()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions { MaxIdleLifetime = TimeSpan.FromMinutes(1), MaxConnectionLifetime = TimeSpan.Zero, MaxMessagesPerConnection = 0 };
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(options, clock, created);
        _ = created[0].IsConnected.Returns(true);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2)); // aged out
        await CreateStrategy(options, clock).PrepareAsync(connection, CancellationToken.None);

        Assert.Equal(2, created.Count); // fresh transport
        await created[1].Received(1).ConnectAsync(Host.Host, Host.Port, Host.Security, Arg.Any<CancellationToken>());
        await created[1].Received(1).AuthenticateAsync(Credentials.UserName, Credentials.Password, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_Throws_When_Item_Is_Null() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await CreateStrategy(new SmtpClientOptions(), new FakeTimeProvider()).PrepareAsync(null!, CancellationToken.None));
}

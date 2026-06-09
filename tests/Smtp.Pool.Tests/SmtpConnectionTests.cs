using MailKit;
using Microsoft.Extensions.Time.Testing;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Diagnostics.CodeAnalysis;

namespace Smtp.Pool.Tests;

public sealed class SmtpConnectionTests
{
    private static readonly SmtpHostOptions Host = new() { Host = "smtp.example.test", Port = 587 };

    // Each limit isolated by disabling the other two (zero = no limit).
    private static SmtpClientOptions OnlyMessages(int max) =>
        new() { MaxMessagesPerConnection = max, MaxConnectionLifetime = TimeSpan.Zero, MaxIdleLifetime = TimeSpan.Zero };

    private static SmtpClientOptions OnlyLifetime(TimeSpan max) =>
        new() { MaxConnectionLifetime = max, MaxIdleLifetime = TimeSpan.Zero, MaxMessagesPerConnection = 0 };

    private static SmtpClientOptions OnlyIdle(TimeSpan max) =>
        new() { MaxIdleLifetime = max, MaxConnectionLifetime = TimeSpan.Zero, MaxMessagesPerConnection = 0 };

    private static SmtpConnection CreateConnection(SmtpClientOptions options, FakeTimeProvider clock, List<IMailTransport> created) =>
        new(() =>
        {
            var transport = Substitute.For<IMailTransport>();
            created.Add(transport);
            return transport;
        }, options, clock);

    [Fact]
    public void Fresh_Connection_Is_Never_Recyclable()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyMessages(1), clock, []);

        clock.Advance(TimeSpan.FromDays(365));

        Assert.False(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Recycles_After_Max_Messages()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyMessages(3), clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);
        using var message = new MimeMessage();

        await connection.SendAsync(message, CancellationToken.None);
        await connection.SendAsync(message, CancellationToken.None);
        Assert.False(connection.ShouldRecycle(clock.GetUtcNow()));

        await connection.SendAsync(message, CancellationToken.None);
        Assert.True(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Recycles_After_Max_Connection_Lifetime()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyLifetime(TimeSpan.FromMinutes(20)), clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(1));
        Assert.False(connection.ShouldRecycle(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Recycles_After_Max_Idle_Lifetime()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyIdle(TimeSpan.FromMinutes(10)), clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Activity_Resets_The_Idle_Clock()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyIdle(TimeSpan.FromMinutes(10)), clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);
        using var message = new MimeMessage();

        clock.Advance(TimeSpan.FromMinutes(9));
        await connection.SendAsync(message, CancellationToken.None); // resets the idle clock

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(connection.ShouldRecycle(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Recycles_On_Whichever_Limit_Comes_First()
    {
        var clock = new FakeTimeProvider();
        var options = new SmtpClientOptions
        {
            MaxIdleLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionLifetime = TimeSpan.FromMinutes(20),
            MaxMessagesPerConnection = 100,
        };
        using var connection = CreateConnection(options, clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(10)); // idle hits before age or message count

        Assert.True(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task RecycleAsync_Quits_Disposes_And_Replaces_The_Transport()
    {
        var clock = new FakeTimeProvider();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(OnlyMessages(1), clock, created);
        await connection.ConnectAsync(Host, CancellationToken.None);
        _ = created[0].IsConnected.Returns(true);

        await connection.RecycleAsync(CancellationToken.None);

        Assert.Equal(2, created.Count);
        await created[0].Received(1).DisconnectAsync(true, Arg.Any<CancellationToken>());
        created[0].Received(1).Dispose();
        Assert.False(connection.ShouldRecycle(clock.GetUtcNow().AddYears(1))); // reset to unconnected
    }

    [Fact]
    public void Dispose_Quits_And_Disposes_The_Transport()
    {
        var created = new List<IMailTransport>();
        using (var connection = CreateConnection(OnlyMessages(1), new FakeTimeProvider(), created))
        {
            _ = created[0].IsConnected.Returns(true);
        }

        created[0].Received(1).Disconnect(true, Arg.Any<CancellationToken>());
        created[0].Received(1).Dispose();
    }

    [Fact]
    public async Task SendAsync_Forwards_To_The_Transport()
    {
        var clock = new FakeTimeProvider();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(OnlyMessages(0), clock, created);
        await connection.ConnectAsync(Host, CancellationToken.None);
        using var message = new MimeMessage();

        await connection.SendAsync(message, CancellationToken.None);

        _ = await created[0].Received(1).SendAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_Throws_When_Message_Is_Null()
    {
        using var connection = CreateConnection(OnlyMessages(0), new FakeTimeProvider(), []);

        _ = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await connection.SendAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task PingAsync_Returns_True_And_Records_Activity()
    {
        var clock = new FakeTimeProvider();
        using var connection = CreateConnection(OnlyIdle(TimeSpan.FromMinutes(10)), clock, []);
        await connection.ConnectAsync(Host, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(await connection.PingAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromMinutes(9)); // idle measured from the ping, not the connect
        Assert.False(connection.ShouldRecycle(clock.GetUtcNow()));
    }

    [Fact]
    public async Task PingAsync_Returns_False_When_NoOp_Throws()
    {
        var clock = new FakeTimeProvider();
        var created = new List<IMailTransport>();
        using var connection = CreateConnection(OnlyMessages(0), clock, created);
        await connection.ConnectAsync(Host, CancellationToken.None);
        _ = created[0].NoOpAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("dropped"));

        Assert.False(await connection.PingAsync(CancellationToken.None));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no connection is constructed to dispose")]
    public void Ctor_Null_TransportFactory_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnection(null!, new SmtpClientOptions(), new FakeTimeProvider()));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no connection is constructed to dispose")]
    public void Ctor_Null_Options_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnection(() => null!, null!, new FakeTimeProvider()));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no connection is constructed to dispose")]
    public void Ctor_Null_TimeProvider_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnection(() => null!, new SmtpClientOptions(), null!));
}

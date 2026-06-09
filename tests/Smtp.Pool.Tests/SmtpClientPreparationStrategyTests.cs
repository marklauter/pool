using MailKit;
using MailKit.Security;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Smtp.Pool.Tests;

public sealed class SmtpClientPreparationStrategyTests
{
    private const string Host = "smtp.example.test";
    private const int Port = 587;
    private const SecureSocketOptions Security = SecureSocketOptions.StartTls;
    private const string UserName = "user";
    private const string Password = "pass";

    private static SmtpClientPreparationStrategy CreateStrategy() =>
        new(
            Options.Create(new SmtpHostOptions { Host = Host, Port = Port, Security = Security }),
            Options.Create(new SmtpClientCredentials { UserName = UserName, Password = Password }));

    [Fact]
    public async Task IsReadyAsync_Returns_True_When_Connected_Authenticated_And_NoOp_Succeeds()
    {
        using var transport = Substitute.For<IMailTransport>();
        _ = transport.IsConnected.Returns(true);
        _ = transport.IsAuthenticated.Returns(true);

        var ready = await CreateStrategy().IsReadyAsync(transport, CancellationToken.None);

        Assert.True(ready);
    }

    [Fact]
    public async Task IsReadyAsync_Returns_False_When_Item_Is_Null()
    {
        var ready = await CreateStrategy().IsReadyAsync(null!, CancellationToken.None);

        Assert.False(ready);
    }

    [Fact]
    public async Task IsReadyAsync_Returns_False_When_Not_Connected()
    {
        using var transport = Substitute.For<IMailTransport>();
        _ = transport.IsConnected.Returns(false);
        _ = transport.IsAuthenticated.Returns(true);

        var ready = await CreateStrategy().IsReadyAsync(transport, CancellationToken.None);

        Assert.False(ready);
    }

    [Fact]
    public async Task IsReadyAsync_Returns_False_When_Not_Authenticated()
    {
        using var transport = Substitute.For<IMailTransport>();
        _ = transport.IsConnected.Returns(true);
        _ = transport.IsAuthenticated.Returns(false);

        var ready = await CreateStrategy().IsReadyAsync(transport, CancellationToken.None);

        Assert.False(ready);
    }

    [Fact]
    public async Task IsReadyAsync_Returns_False_When_NoOp_Throws()
    {
        using var transport = Substitute.For<IMailTransport>();
        _ = transport.IsConnected.Returns(true);
        _ = transport.IsAuthenticated.Returns(true);
        _ = transport.NoOpAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("dropped"));

        var ready = await CreateStrategy().IsReadyAsync(transport, CancellationToken.None);

        Assert.False(ready);
    }

    [Fact]
    public async Task PrepareAsync_Connects_And_Authenticates_Using_The_Configured_Values()
    {
        using var transport = Substitute.For<IMailTransport>();

        await CreateStrategy().PrepareAsync(transport, CancellationToken.None);

        await transport.Received(1).ConnectAsync(Host, Port, Security, Arg.Any<CancellationToken>());
        await transport.Received(1).AuthenticateAsync(UserName, Password, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PrepareAsync_Throws_When_Item_Is_Null() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await CreateStrategy().PrepareAsync(null!, CancellationToken.None));

    [Fact]
    public void Ctor_Null_HostOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpClientPreparationStrategy(null!, Options.Create(new SmtpClientCredentials())));

    [Fact]
    public void Ctor_Null_Credentials_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpClientPreparationStrategy(Options.Create(new SmtpHostOptions()), null!));
}

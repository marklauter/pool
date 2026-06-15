using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;

namespace Smtp.Pool.Tests;

public sealed class SmtpConnectionFactoryTests
{
    private static SmtpConnectionFactory CreateFactory(
        SmtpClientOptions? clientOptions = null,
        SmtpHostOptions? hostOptions = null) =>
        new(
            Options.Create(clientOptions ?? new SmtpClientOptions()),
            Options.Create(hostOptions ?? new SmtpHostOptions { Host = "smtp.example.test" }),
            TimeProvider.System);

    [Fact]
    public void CreateItem_Returns_An_SmtpConnection()
    {
        using var connection = CreateFactory().CreateItem();

        Assert.NotNull(connection);
    }

    [Fact]
    public void CreateTransport_Applies_The_Configured_Timeout()
    {
        using var transport = CreateFactory(new SmtpClientOptions { TimeoutMilliseconds = 42_000 }).CreateTransport();

        Assert.Equal(42_000, transport.Timeout);
    }

    [Fact]
    public void CreateTransport_Validates_Certificates_By_Default()
    {
        using var transport = CreateFactory().CreateTransport();

        var client = Assert.IsType<SmtpClient>(transport);
        Assert.Null(client.ServerCertificateValidationCallback);
        Assert.True(client.CheckCertificateRevocation);
    }

    [Fact]
    public void CreateTransport_Relaxes_Certificate_Policy_When_Opted_Out()
    {
        var hostOptions = new SmtpHostOptions
        {
            Host = "smtp.example.test",
            RequireValidCertificate = false,
            CheckCertificateRevocation = false,
        };

        using var transport = CreateFactory(hostOptions: hostOptions).CreateTransport();

        var client = Assert.IsType<SmtpClient>(transport);
        Assert.NotNull(client.ServerCertificateValidationCallback);
        Assert.False(client.CheckCertificateRevocation);
    }

    [Fact]
    public void Ctor_Null_ClientOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnectionFactory(null!, Options.Create(new SmtpHostOptions()), TimeProvider.System));

    [Fact]
    public void Ctor_Null_HostOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnectionFactory(Options.Create(new SmtpClientOptions()), null!, TimeProvider.System));

    [Fact]
    public void Ctor_Null_TimeProvider_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpConnectionFactory(Options.Create(new SmtpClientOptions()), Options.Create(new SmtpHostOptions()), null!));
}

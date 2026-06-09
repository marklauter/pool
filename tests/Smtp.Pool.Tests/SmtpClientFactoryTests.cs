using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;

namespace Smtp.Pool.Tests;

public sealed class SmtpClientFactoryTests
{
    private static SmtpClientFactory CreateFactory(
        SmtpClientOptions? clientOptions = null,
        SmtpHostOptions? hostOptions = null) =>
        new(
            Options.Create(clientOptions ?? new SmtpClientOptions()),
            Options.Create(hostOptions ?? new SmtpHostOptions { Host = "smtp.example.test" }));

    [Fact]
    public void CreateItem_Returns_An_SmtpClient()
    {
        using var item = CreateFactory().CreateItem();

        _ = Assert.IsType<SmtpClient>(item);
    }

    [Fact]
    public void CreateItem_Applies_The_Configured_Timeout()
    {
        using var item = CreateFactory(new SmtpClientOptions { TimeoutMilliseconds = 42_000 }).CreateItem();

        Assert.Equal(42_000, item.Timeout);
    }

    [Fact]
    public void CreateItem_Validates_Certificates_By_Default()
    {
        using var item = CreateFactory().CreateItem();

        var client = Assert.IsType<SmtpClient>(item);
        Assert.Null(client.ServerCertificateValidationCallback);
        Assert.True(client.CheckCertificateRevocation);
    }

    [Fact]
    public void CreateItem_Relaxes_Certificate_Policy_When_Opted_Out()
    {
        var hostOptions = new SmtpHostOptions
        {
            Host = "smtp.example.test",
            RequireValidCertificate = false,
            CheckCertificateRevocation = false,
        };

        using var item = CreateFactory(hostOptions: hostOptions).CreateItem();

        var client = Assert.IsType<SmtpClient>(item);
        Assert.NotNull(client.ServerCertificateValidationCallback);
        Assert.False(client.CheckCertificateRevocation);
    }

    [Fact]
    public void Ctor_Null_ClientOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpClientFactory(null!, Options.Create(new SmtpHostOptions())));

    [Fact]
    public void Ctor_Null_HostOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new SmtpClientFactory(Options.Create(new SmtpClientOptions()), null!));
}

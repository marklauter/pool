using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;

namespace Smtp.Pool.Tests;

public sealed class SmtpClientFactoryTests
{
    [Fact]
    public void CreateItem_Returns_An_SmtpClient()
    {
        var factory = new SmtpClientFactory(Options.Create(new SmtpClientOptions()));

        using var item = factory.CreateItem();

        _ = Assert.IsType<SmtpClient>(item);
    }

    [Fact]
    public void CreateItem_Applies_The_Configured_Timeout()
    {
        var factory = new SmtpClientFactory(Options.Create(new SmtpClientOptions { TimeoutMilliseconds = 42_000 }));

        using var item = factory.CreateItem();

        Assert.Equal(42_000, item.Timeout);
    }

    [Fact]
    public void Ctor_Null_Options_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SmtpClientFactory(null!));
}

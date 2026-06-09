using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pool;

namespace Smtp.Pool.Tests;

public sealed class SmtpClientPoolServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SmtpHostOptions:Host"] = "smtp.example.test",
                ["SmtpHostOptions:Port"] = "587",
                ["SmtpHostOptions:Security"] = "StartTls",
                ["SmtpClientCredentials:UserName"] = "user",
                ["SmtpClientCredentials:Password"] = "pass",
                ["PoolOptions:MaxSize"] = "4",
            })
            .Build();

    [Fact]
    public void AddSmtpClientPool_Registers_Pool_Factory_And_Strategy()
    {
        var services = new ServiceCollection();

        _ = services.AddSmtpClientPool(BuildConfiguration());

        Assert.Contains(services, d => d.ServiceType == typeof(IPool<SmtpConnection>));
        Assert.Contains(services, d => d.ServiceType == typeof(IItemFactory<SmtpConnection>));
        Assert.Contains(services, d => d.ServiceType == typeof(IPreparationStrategy<SmtpConnection>));
        Assert.Contains(services, d => d.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void AddSmtpClientPool_Honors_The_Configure_Override()
    {
        var services = new ServiceCollection();
        var maxSize = 0;

        _ = services.AddSmtpClientPool(BuildConfiguration(), options => maxSize = options.MaxSize);

        Assert.Equal(4, maxSize);
    }

    [Fact]
    public void AddSmtpClientPool_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddSmtpClientPool(BuildConfiguration()));

    [Fact]
    public void AddSmtpClientPool_Null_Configuration_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddSmtpClientPool(null!));
}

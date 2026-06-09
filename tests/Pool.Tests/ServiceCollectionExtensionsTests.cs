using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pool.Metrics;
using Pool.Tests.Fakes;

namespace Pool.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration EmptyConfiguration() => Configuration([]);

    // --- AddPool<TPoolItem> ---

    [Fact]
    public void AddPool_With_Defaults_Resolves_Pool_And_Default_Services()
    {
        var configuration = Configuration(new()
        {
            ["PoolOptions:UseDefaultFactory"] = "true",
            ["PoolOptions:UseDefaultPreparationStrategy"] = "true",
        });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddScoped<IEcho, Echo>()
            .AddPool<IEcho>(configuration)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IPool<IEcho>>());
        Assert.NotNull(provider.GetRequiredService<IItemFactory<IEcho>>());
        Assert.NotNull(provider.GetRequiredService<IPreparationStrategy<IEcho>>());
        Assert.NotNull(provider.GetRequiredService<IPoolMetrics>());
    }

    [Fact]
    public void AddPool_Without_Defaults_Does_Not_Register_Factory_Or_Strategy()
    {
        // UseDefaultFactory and UseDefaultPreparationStrategy default to false
        var services = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(EmptyConfiguration());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IItemFactory<IEcho>));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPreparationStrategy<IEcho>));
    }

    [Fact]
    public void AddPool_Without_Section_Binds_Defaults()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(EmptyConfiguration())
            .BuildServiceProvider();

        // no PoolOptions section -> eager bind falls back to a new PoolOptions, defaults flow through
        Assert.Equal(100, provider.GetRequiredService<PoolOptions>().MaxSize);
    }

    [Fact]
    public void AddPool_ConfigureOptions_Is_Applied()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(EmptyConfiguration(), options => options.MaxSize = 7)
            .BuildServiceProvider();

        Assert.Equal(7, provider.GetRequiredService<PoolOptions>().MaxSize);
    }

    [Fact]
    public void AddPool_Invalid_MaxSize_Throws_On_Resolve()
    {
        var configuration = Configuration(new() { ["PoolOptions:MaxSize"] = "0" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(configuration)
            .BuildServiceProvider();

        // [Range(1, int.MaxValue)] on MaxSize fails when the validated options are materialized
        _ = Assert.Throws<OptionsValidationException>(provider.GetRequiredService<PoolOptions>);
    }

    [Fact]
    public void AddPool_Negative_MinSize_Throws_On_Resolve()
    {
        var configuration = Configuration(new() { ["PoolOptions:MinSize"] = "-1" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(configuration)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(provider.GetRequiredService<PoolOptions>);
    }

    [Fact]
    public void AddPool_ConfigureOptions_Producing_Invalid_Value_Throws_On_Resolve()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddPool<IEcho>(EmptyConfiguration(), options => options.MaxSize = 0)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(provider.GetRequiredService<PoolOptions>);
    }

    [Fact]
    public void AddPool_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddPool<IEcho>(EmptyConfiguration()));

    [Fact]
    public void AddPool_Null_Configuration_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddPool<IEcho>(null!));

    // --- AddPreparationStrategy / AddPoolItemFactory ---

    [Fact]
    public void AddPreparationStrategy_Registers_Implementation()
    {
        var services = new ServiceCollection()
            .AddPreparationStrategy<IEcho, EchoPreparationStrategy>();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IPreparationStrategy<IEcho>)
            && d.ImplementationType == typeof(EchoPreparationStrategy));
    }

    [Fact]
    public void AddPreparationStrategy_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddPreparationStrategy<IEcho, EchoPreparationStrategy>());

    [Fact]
    public void AddPoolItemFactory_Registers_Implementation()
    {
        var services = new ServiceCollection()
            .AddPoolItemFactory<IEcho, EchoFactory>();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IItemFactory<IEcho>)
            && d.ImplementationType == typeof(EchoFactory));
    }

    [Fact]
    public void AddPoolItemFactory_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddPoolItemFactory<IEcho, EchoFactory>());

    // --- AddDefaultPoolMetrics ---

    [Fact]
    public void AddDefaultPoolMetrics_Resolves_Metrics()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultPoolMetrics<IEcho>()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IPoolMetrics>());
    }

    [Fact]
    public void AddDefaultPoolMetrics_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDefaultPoolMetrics<IEcho>());
}

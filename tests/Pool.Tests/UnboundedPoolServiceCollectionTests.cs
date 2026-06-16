using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class UnboundedPoolServiceCollectionTests
{
    private sealed class PoolItem;

    private sealed class PoolClient(IPool<PoolItem> pool)
    {
        public IPool<PoolItem> Pool { get; } = pool ?? throw new ArgumentNullException(nameof(pool));
        public bool Configured { get; set; }
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration EmptyConfiguration() => Configuration([]);

    // --- AddUnboundedPool<TPoolItem> ---

    [Fact]
    public void AddUnboundedPool_With_Defaults_Resolves_Pool_And_Default_Services()
    {
        var configuration = Configuration(new()
        {
            ["UnboundedPoolOptions:UseDefaultFactory"] = "true",
            ["UnboundedPoolOptions:UseDefaultPreparationStrategy"] = "true",
        });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddScoped<IEcho, Echo>()
            .AddUnboundedPool<IEcho>(configuration)
            .BuildServiceProvider();

        var pool = provider.GetRequiredService<IPool<IEcho>>();
        _ = Assert.IsType<UnboundedPool<IEcho>>(pool);
        Assert.NotNull(provider.GetRequiredService<IItemFactory<IEcho>>());
        Assert.NotNull(provider.GetRequiredService<IPreparationStrategy<IEcho>>());
        Assert.NotNull(provider.GetRequiredService<IPoolMetrics>());
    }

    [Fact]
    public void AddUnboundedPool_Without_Defaults_Does_Not_Register_Factory_Or_Strategy()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddUnboundedPool<IEcho>(EmptyConfiguration());

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IItemFactory<IEcho>));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPreparationStrategy<IEcho>));
    }

    [Fact]
    public void AddUnboundedPool_Without_Section_Binds_Defaults()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddUnboundedPool<IEcho>(EmptyConfiguration())
            .BuildServiceProvider();

        Assert.Equal(100, provider.GetRequiredService<UnboundedPoolOptions>().MaxIdle);
    }

    [Fact]
    public void AddUnboundedPool_ConfigureOptions_Is_Applied()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddUnboundedPool<IEcho>(EmptyConfiguration(), options => options.MaxIdle = 7)
            .BuildServiceProvider();

        Assert.Equal(7, provider.GetRequiredService<UnboundedPoolOptions>().MaxIdle);
    }

    [Fact]
    public void AddUnboundedPool_Negative_MaxIdle_Throws_On_Resolve()
    {
        var configuration = Configuration(new() { ["UnboundedPoolOptions:MaxIdle"] = "-1" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddUnboundedPool<IEcho>(configuration)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(provider.GetRequiredService<UnboundedPoolOptions>);
    }

    [Fact]
    public void AddUnboundedPool_Negative_MinSize_Throws_On_Resolve()
    {
        var configuration = Configuration(new() { ["UnboundedPoolOptions:MinSize"] = "-1" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddUnboundedPool<IEcho>(configuration)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(provider.GetRequiredService<UnboundedPoolOptions>);
    }

    [Fact]
    public void AddUnboundedPool_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddUnboundedPool<IEcho>(EmptyConfiguration()));

    [Fact]
    public void AddUnboundedPool_Null_Configuration_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddUnboundedPool<IEcho>(null!));

    // --- AddUnboundedPool<TPoolItem, TClient> (named, with client) ---

    [Fact]
    public void AddUnboundedPool_With_Client_Registration_Succeeds()
    {
        var configuration = Configuration(new()
        {
            ["UnboundedPoolOptions:UseDefaultFactory"] = "true",
            ["UnboundedPoolOptions:UseDefaultPreparationStrategy"] = "true",
        });

        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddUnboundedPool<PoolItem, PoolClient>(configuration)
            .BuildServiceProvider();

        var client = services.GetRequiredService<PoolClient>();
        Assert.NotNull(client.Pool);
        _ = Assert.IsType<UnboundedPool<PoolItem>>(client.Pool);
    }

    [Fact]
    public void AddUnboundedPool_With_Client_Runs_ConfigureClient()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddUnboundedPool<PoolItem, PoolClient>(
                EmptyConfiguration(),
                configureOptions: options => options.UseDefaultFactory = true,
                configureClient: client => client.Configured = true)
            .BuildServiceProvider();

        var client = services.GetRequiredService<PoolClient>();
        Assert.True(client.Configured);
    }

    // --- AddNamedUnboundedPool<TPoolItem> ---

    [Fact]
    public void AddNamedUnboundedPool_ConfigureOptions_Is_Applied()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("alpha");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedUnboundedPool<PoolItem>("alpha", EmptyConfiguration(), options => options.MaxIdle = 9)
            .BuildServiceProvider();

        Assert.Equal(9, provider.GetRequiredKeyedService<UnboundedPoolOptions>(serviceKey).MaxIdle);
    }

    [Fact]
    public void AddNamedUnboundedPool_PoolSpecific_Section_Overrides_General()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("beta");
        var configuration = Configuration(new()
        {
            ["UnboundedPoolOptions:MaxIdle"] = "5",
            [$"{serviceKey}_UnboundedPoolOptions:MaxIdle"] = "11",
        });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedUnboundedPool<PoolItem>("beta", configuration)
            .BuildServiceProvider();

        Assert.Equal(11, provider.GetRequiredKeyedService<UnboundedPoolOptions>(serviceKey).MaxIdle);
    }

    [Fact]
    public void AddNamedUnboundedPool_Without_Sections_Uses_Defaults()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("gamma");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedUnboundedPool<PoolItem>("gamma", EmptyConfiguration())
            .BuildServiceProvider();

        Assert.Equal(100, provider.GetRequiredKeyedService<UnboundedPoolOptions>(serviceKey).MaxIdle);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "the keyed pool is a singleton owned by the ServiceProvider; it is disposed when the provider is disposed")]
    public void AddNamedUnboundedPool_With_Defaults_Resolves_Keyed_Pool()
    {
        var serviceKey = ServiceKey.Create<IEcho>("zeta");
        var configuration = Configuration(new()
        {
            ["UnboundedPoolOptions:UseDefaultFactory"] = "true",
            ["UnboundedPoolOptions:UseDefaultPreparationStrategy"] = "true",
        });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddNamedUnboundedPool<IEcho>("zeta", configuration)
            .BuildServiceProvider();

        var pool = provider.GetRequiredService<IPoolFactory<IEcho>>().CreatePool(serviceKey);
        _ = Assert.IsType<UnboundedPool<IEcho>>(pool);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "the keyed pool is a singleton owned by the ServiceProvider; it is disposed when the provider is disposed")]
    public void AddNamedUnboundedPool_Falls_Back_To_NonKeyed_Services_When_Defaults_Off()
    {
        var serviceKey = ServiceKey.Create<IEcho>("delta");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IItemFactory<IEcho>, EchoFactory>()
            .AddSingleton<IPreparationStrategy<IEcho>, EchoPreparationStrategy>()
            .AddNamedUnboundedPool<IEcho>("delta", EmptyConfiguration())
            .BuildServiceProvider();

        var pool = provider.GetRequiredService<IPoolFactory<IEcho>>().CreatePool(serviceKey);
        Assert.NotNull(pool);
    }

    [Fact]
    public void AddNamedUnboundedPool_Invalid_Options_Throws_On_Resolve()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("epsilon");
        var configuration = Configuration(new() { ["UnboundedPoolOptions:MaxIdle"] = "-1" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedUnboundedPool<PoolItem>("epsilon", configuration)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<UnboundedPoolOptions>(serviceKey));
    }

    [Fact]
    public void AddNamedUnboundedPool_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddNamedUnboundedPool<PoolItem>("name", EmptyConfiguration()));

    [Fact]
    public void AddNamedUnboundedPool_Null_Name_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddNamedUnboundedPool<PoolItem>(null!, EmptyConfiguration()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddNamedUnboundedPool_Whitespace_Name_Throws(string name) =>
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddNamedUnboundedPool<PoolItem>(name, EmptyConfiguration()));

    [Fact]
    public void AddNamedUnboundedPool_Null_Configuration_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddNamedUnboundedPool<PoolItem>("name", null!));

    // --- AddDefaultUnboundedPoolMetrics ---

    [Fact]
    public void AddDefaultUnboundedPoolMetrics_Resolves_Metrics()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddDefaultUnboundedPoolMetrics<IEcho>()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IPoolMetrics>());
    }

    [Fact]
    public void AddDefaultUnboundedPoolMetrics_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDefaultUnboundedPoolMetrics<IEcho>());

    [Fact]
    public void AddDefaultUnboundedPoolMetrics_Named_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDefaultUnboundedPoolMetrics<PoolItem>("name"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDefaultUnboundedPoolMetrics_Named_Whitespace_Name_Throws(string name) =>
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddDefaultUnboundedPoolMetrics<PoolItem>(name));
}

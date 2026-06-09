using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class NamedPoolTests
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

    [Fact]
    public void NamedPool_Registration_Succeeds()
    {
        var configuration = Configuration(new()
        {
            ["PoolOptions:UseDefaultFactory"] = "true",
            ["PoolOptions:UseDefaultPreparationStrategy"] = "true",
            ["PoolOptions:MinSize"] = "1",
            ["PoolOptions:MaxSize"] = "2",
            ["PoolOptions:LeaseTimeout"] = "00:00:00.01",
            ["PoolOptions:PreparationTimeout"] = "00:00:00.01",
        });

        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddPool<PoolItem, PoolClient>(configuration)
            .BuildServiceProvider();

        var client = services.GetRequiredService<PoolClient>();
        Assert.NotNull(client.Pool);
    }

    [Fact]
    public void AddPool_With_Client_Runs_ConfigureClient()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddPool<PoolItem, PoolClient>(
                EmptyConfiguration(),
                configureOptions: options => options.UseDefaultFactory = true,
                configureClient: client => client.Configured = true)
            .BuildServiceProvider();

        var client = services.GetRequiredService<PoolClient>();
        Assert.True(client.Configured);
    }

    [Fact]
    public void AddNamedPool_ConfigureOptions_Is_Applied()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("alpha");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedPool<PoolItem>("alpha", EmptyConfiguration(), options => options.MaxSize = 9)
            .BuildServiceProvider();

        Assert.Equal(9, provider.GetRequiredKeyedService<PoolOptions>(serviceKey).MaxSize);
    }

    [Fact]
    public void AddNamedPool_PoolSpecific_Section_Overrides_General()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("beta");
        var configuration = Configuration(new()
        {
            ["PoolOptions:MaxSize"] = "5",                  // general section
            [$"{serviceKey}_PoolOptions:MaxSize"] = "11",   // pool-specific override
        });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedPool<PoolItem>("beta", configuration)
            .BuildServiceProvider();

        Assert.Equal(11, provider.GetRequiredKeyedService<PoolOptions>(serviceKey).MaxSize);
    }

    [Fact]
    public void AddNamedPool_Without_Sections_Uses_Defaults()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("gamma");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedPool<PoolItem>("gamma", EmptyConfiguration())
            .BuildServiceProvider();

        Assert.Equal(100, provider.GetRequiredKeyedService<PoolOptions>(serviceKey).MaxSize);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "the keyed pool is a singleton owned by the ServiceProvider; it is disposed when the provider is disposed")]
    public void AddNamedPool_Falls_Back_To_NonKeyed_Services_When_Defaults_Off()
    {
        var serviceKey = ServiceKey.Create<IEcho>("delta");

        // UseDefaultFactory / UseDefaultPreparationStrategy default to false, so no keyed factory or
        // strategy is registered; resolving the keyed pool runs the factory lambda, which must fall
        // back to the non-keyed registrations below (and skip the default-strategy/factory branches).
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IItemFactory<IEcho>, EchoFactory>()
            .AddSingleton<IPreparationStrategy<IEcho>, EchoPreparationStrategy>()
            .AddNamedPool<IEcho>("delta", EmptyConfiguration())
            .BuildServiceProvider();

        var pool = provider.GetRequiredService<IPoolFactory<IEcho>>().CreatePool(serviceKey);
        Assert.NotNull(pool);
    }

    [Fact]
    public void AddNamedPool_Invalid_Options_Throws_On_Resolve()
    {
        var serviceKey = ServiceKey.Create<PoolItem>("epsilon");
        var configuration = Configuration(new() { ["PoolOptions:MaxSize"] = "0" });

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddTransient<PoolItem>()
            .AddNamedPool<PoolItem>("epsilon", configuration)
            .BuildServiceProvider();

        _ = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredKeyedService<PoolOptions>(serviceKey));
    }

    [Fact]
    public void AddPoolFactory_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddPoolFactory<PoolItem>());

    [Fact]
    public void AddNamedPool_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddNamedPool<PoolItem>("name", EmptyConfiguration()));

    [Fact]
    public void AddNamedPool_Null_Name_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddNamedPool<PoolItem>(null!, EmptyConfiguration()));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddNamedPool_Whitespace_Name_Throws(string name) =>
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddNamedPool<PoolItem>(name, EmptyConfiguration()));

    [Fact]
    public void AddNamedPool_Null_Configuration_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new ServiceCollection().AddNamedPool<PoolItem>("name", null!));

    [Fact]
    public void AddDefaultPoolMetrics_Named_Null_Services_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddDefaultPoolMetrics<PoolItem>("name"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddDefaultPoolMetrics_Named_Whitespace_Name_Throws(string name) =>
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddDefaultPoolMetrics<PoolItem>(name));
}

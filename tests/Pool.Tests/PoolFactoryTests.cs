using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class PoolFactoryTests
{
    [Fact]
    public void Ctor_Null_ServiceProvider_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new PoolFactory<IEcho>(null!));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "CreatePool throws on an invalid key; no pool is created")]
    public void CreatePool_Null_Key_Throws()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new PoolFactory<IEcho>(provider);

        _ = Assert.Throws<ArgumentNullException>(() => factory.CreatePool(null!));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "CreatePool throws on an invalid key; no pool is created")]
    public void CreatePool_Empty_Key_Throws()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var factory = new PoolFactory<IEcho>(provider);

        _ = Assert.Throws<ArgumentException>(() => factory.CreatePool(""));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "the keyed pools are singletons owned by the ServiceProvider")]
    public void CreatePool_Returns_Same_Instance_For_Same_Key()
    {
        var serviceKey = ServiceKey.Create<IEcho>("zeta");

        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IItemFactory<IEcho>, EchoFactory>()
            .AddSingleton<IPreparationStrategy<IEcho>, EchoPreparationStrategy>()
            .AddNamedPool<IEcho>("zeta", new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        var factory = provider.GetRequiredService<IPoolFactory<IEcho>>();
        var first = factory.CreatePool(serviceKey);
        var second = factory.CreatePool(serviceKey);

        Assert.Same(first, second);
    }
}

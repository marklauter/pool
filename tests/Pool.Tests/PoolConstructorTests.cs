using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class PoolConstructorTests(IPoolMetrics metrics)
{
    private static readonly ILogger<Pool<IEcho>> Logger = NullLogger<Pool<IEcho>>.Instance;

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_MaxSize_Below_One_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Pool<IEcho>(new EchoFactory(), Logger, metrics, new PoolOptions { MaxSize = 0 }));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_Negative_MinSize_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Pool<IEcho>(new EchoFactory(), Logger, metrics, new PoolOptions { MinSize = -1 }));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_Disposes_Already_Created_Items_When_Factory_Throws_Mid_Seed()
    {
        // MinSize=5 but the factory throws on the 3rd create; the two already-created items
        // must be disposed before the exception propagates (findings I4)
        var factory = new ThrowAfterCountItemFactory(throwAfter: 2);
        var options = new PoolOptions
        {
            MinSize = 5,
            MaxSize = 10,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        _ = Assert.Throws<InvalidOperationException>(
            () => new Pool<IEcho>(factory, Logger, metrics, options));

        Assert.Equal(2, factory.CreatedItems.Count);
        Assert.All(factory.CreatedItems, item => Assert.True(item.IsDisposed()));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_Null_ItemFactory_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new Pool<IEcho>(null!, Logger, metrics, new PoolOptions()));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_Null_Logger_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new Pool<IEcho>(new EchoFactory(), null!, metrics, new PoolOptions()));

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "the constructor throws; no pool is constructed to dispose")]
    public void Ctor_Null_Metrics_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new Pool<IEcho>(new EchoFactory(), Logger, null!, new PoolOptions()));
}

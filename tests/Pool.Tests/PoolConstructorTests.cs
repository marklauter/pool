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
}

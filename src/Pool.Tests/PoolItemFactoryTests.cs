using Microsoft.Extensions.DependencyInjection;
using Pool.DefaultStrategies;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class PoolItemFactoryTests
{
    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "required for test")]
    public async Task PoolItemFactory_Doesnt_Crash_On_Dispose()
    {
        using var services = new ServiceCollection()
            .AddScoped<IEcho, Echo>()
            .BuildServiceProvider();

        var factory = new DefaultItemFactory<IEcho>(services);
        var pool = new Pool<IEcho>(
            new DefaultPoolMetrics(Pool<IEcho>.PoolName),
            factory,
            new DefaultPreparationStrategy<IEcho>(),
            new PoolOptions { MinSize = 5 });
        var item = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(item);

        await pool.ReleaseAsync(item);

        // pool disposes all the items on the queue
        pool.Dispose();

        // factory disposes the scope, which also disposes the items, so the items need to protect themselves with dispose pattern
        factory.Dispose();
    }
}

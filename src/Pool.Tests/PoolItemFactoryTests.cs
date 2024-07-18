using Microsoft.Extensions.DependencyInjection;
using Pool.DefaultStrategies;
using Pool.Tests.Fakes;

namespace Pool.Tests;

public sealed class PoolItemFactoryTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "required for test")]
    public async Task PoolItemFactory_Doesnt_Crash_On_Dispose()
    {
        using var services = new ServiceCollection()
            .AddScoped<IEcho, Echo>()
            .BuildServiceProvider();

        var factory = new DefaultItemFactory<IEcho>(services);
        var pool = new Pool<IEcho>(factory, new DefaultPreparationStrategy<IEcho>(), new PoolOptions { MinSize = 5 });
        var item = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(item);

        await pool.ReleaseAsync(item);

        // pool disposes all the items on the queue
        pool.Dispose();

        // factory disposes the scope, which also disposes the items, so the items need to protect themselves with dispose pattern
        factory.Dispose();
    }
}

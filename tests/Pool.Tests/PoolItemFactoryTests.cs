using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pool.DefaultStrategies;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class PoolItemFactoryTests(IPoolMetrics metrics)
{
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    private static readonly ILogger<Pool<IEcho>> Logger = LoggerFactory.CreateLogger<Pool<IEcho>>();

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "required for test")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "pool and factory are disposed inside the Record.Exception delegate to assert dispose is no-throw; the analyzer cannot track disposal through the lambda")]
    public async Task PoolItemFactory_Doesnt_Crash_On_Dispose()
    {
        using var services = new ServiceCollection()
            .AddScoped<IEcho, Echo>()
            .BuildServiceProvider();

        var factory = new DefaultItemFactory<IEcho>(services);
        var pool = new Pool<IEcho>(
            factory,
            Logger,
            metrics,
            new DefaultPreparationStrategy<IEcho>(),
            new PoolOptions { MinSize = 5 });
        var item = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(item);

        pool.Release(item);

        // disposing the pool (drains and disposes the queued items) and then the factory scope (which
        // disposes them again through the DI scope) must not throw: the item tolerates double-dispose
        var ex = Record.Exception(() =>
        {
            pool.Dispose();
            factory.Dispose();
        });

        Assert.Null(ex);
        Assert.True(item.IsDisposed());
    }
}

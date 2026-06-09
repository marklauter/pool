using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.Metrics;

namespace Pool.Tests;

// each test stands up its own IMeterFactory + DefaultPoolMetrics + Pool so the per-pool observer
// registration and the recorded measurements cannot cross-contaminate between tests.
public sealed class MetricsTests
{
    private static readonly string PoolName = Pool<IEcho>.PoolName;

    [Fact]
    public async Task Lease_Wait_Time_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<double>(meterFactory, PoolName, $"{PoolName}.lease_wait_time");

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(item);

        Assert.NotEmpty(collector.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Preparation_Time_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<double>(meterFactory, PoolName, $"{PoolName}.item_preparation_time");

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new EchoPreparationStrategy(),
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        // an unprepared item forces a real PrepareAsync, which records the preparation time
        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(item);

        Assert.NotEmpty(collector.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Lease_Exception_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<long>(meterFactory, PoolName, $"{PoolName}.lease_exception");

        // a factory that throws on the lease path drives one lease failure
        using var pool = new Pool<IEcho>(
            new ThrowAfterCountItemFactory(throwAfter: 0),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1L, collector.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public async Task Preparation_Exception_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<long>(meterFactory, PoolName, $"{PoolName}.preparation_exception");

        // a strategy that throws on first prepare drives one preparation failure
        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new ThrowOnceEchoPreparationStrategy(),
            new PoolOptions { MinSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1L, collector.GetMeasurementSnapshot().Sum(m => m.Value));
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace Pool.Tests;

// each test stands up its own IMeterFactory + DefaultPoolMetrics + Pool so the per-pool observer
// registration and the recorded measurements cannot cross-contaminate between tests.
public sealed class MetricsTests
{
    // pool identity now rides as the pool.name tag value, not as the meter/instrument name prefix
    private static readonly string PoolName = Pool<IEcho>.PoolName;

    [Fact]
    public async Task Lease_Wait_Time_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<double>(meterFactory, PoolMeter.Name, "pool.lease.wait.duration");

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(item);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        // pool identity travels as a tag so OTEL consumers slice by pool without per-pool meters
        Assert.Equal(PoolName, measurement.Tags["pool.name"]);
    }

    [Fact]
    public async Task Preparation_Time_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<double>(meterFactory, PoolMeter.Name, "pool.item.preparation.duration");

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new EchoPreparationStrategy(),
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        // an unprepared item forces a real PrepareAsync, which records the preparation time
        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(item);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(PoolName, measurement.Tags["pool.name"]);
    }

    [Fact]
    public async Task Lease_Exception_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<long>(meterFactory, PoolMeter.Name, "pool.lease.exceptions");

        // a factory that throws on the lease path drives one lease failure
        using var pool = new Pool<IEcho>(
            new ThrowAfterCountItemFactory(throwAfter: 0),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1L, measurement.Value);
        Assert.Equal(PoolName, measurement.Tags["pool.name"]);
        // error.type slices failures by cause without inflating the metric name
        Assert.Equal(typeof(InvalidOperationException).FullName, measurement.Tags["error.type"]);
    }

    [Fact]
    public async Task Preparation_Exception_Is_Recorded()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var collector = new MetricCollector<long>(meterFactory, PoolMeter.Name, "pool.preparation.exceptions");

        // a strategy that throws on first prepare drives one preparation failure
        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new ThrowOnceEchoPreparationStrategy(),
            new PoolOptions { MinSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1L, measurement.Value);
        Assert.Equal(PoolName, measurement.Tags["pool.name"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, measurement.Tags["error.type"]);
    }

    [Fact]
    public async Task Lease_Wait_Time_Excludes_Preparation_Time()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var waitCollector = new MetricCollector<double>(meterFactory, PoolMeter.Name, "pool.lease.wait.duration");
        using var prepCollector = new MetricCollector<double>(meterFactory, PoolMeter.Name, "pool.item.preparation.duration");

        // preparation consumes a fixed slice of fake time; the lease-wait timer must not include it
        var timeProvider = new FakeTimeProvider();
        var prepDuration = TimeSpan.FromSeconds(5);

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new ClockAdvancingEchoPreparationStrategy(timeProvider, prepDuration),
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false },
            timeProvider);

        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(item);

        var wait = Assert.Single(waitCollector.GetMeasurementSnapshot());
        var prep = Assert.Single(prepCollector.GetMeasurementSnapshot());
        // the lease-wait timer stops before preparation, so it captures none of the prep duration.
        // durations are recorded in seconds (OTEL convention)
        Assert.Equal(0d, wait.Value, 3);
        Assert.Equal(prepDuration.TotalSeconds, prep.Value, 3);
    }

    [Fact]
    public async Task Observable_Instruments_Emit_Pool_State_With_Tag()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var allocated = new MetricCollector<int>(meterFactory, PoolMeter.Name, "pool.items.allocated");
        using var available = new MetricCollector<int>(meterFactory, PoolMeter.Name, "pool.items.available");
        using var active = new MetricCollector<int>(meterFactory, PoolMeter.Name, "pool.leases.active");
        using var queued = new MetricCollector<int>(meterFactory, PoolMeter.Name, "pool.leases.queued");
        using var utilization = new MetricCollector<double>(meterFactory, PoolMeter.Name, "pool.utilization");

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        // hold one lease so the gauges report a fully-utilized, single-item pool
        _ = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        allocated.RecordObservableInstruments();
        available.RecordObservableInstruments();
        active.RecordObservableInstruments();
        queued.RecordObservableInstruments();
        utilization.RecordObservableInstruments();

        AssertObservation(allocated, 1);
        AssertObservation(available, 0);
        AssertObservation(active, 1);
        AssertObservation(queued, 0);
        AssertObservation(utilization, 1d);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test deliberately disposes the pool early to verify the severed callback stops reporting")]
    public void Disposing_Pool_Stops_Observable_Instrument_Reporting()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new DefaultPoolMetrics(PoolName, meterFactory, NullLogger<DefaultPoolMetrics>.Instance);
        using var allocated = new MetricCollector<int>(meterFactory, PoolMeter.Name, "pool.items.allocated");

        // explicit Dispose mid-test exercises the unregistration; the using is an idempotent no-op
        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new PoolOptions { MinSize = 1, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

        // the instrument reports while the pool is alive
        allocated.RecordObservableInstruments();
        Assert.NotEmpty(allocated.GetMeasurementSnapshot());

        pool.Dispose();

        // after disposal the severed callback yields nothing, so a fresh collection records no more
        var countAfterDispose = allocated.GetMeasurementSnapshot().Count;
        allocated.RecordObservableInstruments();
        Assert.Equal(countAfterDispose, allocated.GetMeasurementSnapshot().Count);
    }

    private static void AssertObservation<T>(MetricCollector<T> collector, T expected)
        where T : struct
    {
        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(expected, measurement.Value);
        Assert.Equal(PoolName, measurement.Tags["pool.name"]);
    }
}

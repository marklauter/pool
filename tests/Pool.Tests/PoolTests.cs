using Microsoft.Extensions.Logging;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

// todo: add metrics tests https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#test-custom-metrics

public sealed class PoolTests(IPool<IEcho> pool, IPoolMetrics metrics)
{
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    private static readonly ILogger<Pool<IEcho>> Logger = LoggerFactory.CreateLogger<Pool<IEcho>>();

    [Fact]
    public void Pool_Is_Injected() => Assert.NotNull(pool);

    [Fact]
    public void Allocated_Matches_Min() => Assert.Equal(1, pool.ItemsAllocated);

    [Fact]
    public void Available_Matches_Allocated() => Assert.Equal(pool.ItemsAllocated, pool.ItemsAvailable);

    [Fact]
    public void Backlog_Is_Empty() => Assert.Equal(0, pool.QueuedLeases);

    [Fact]
    public async Task Lease_And_Release()
    {
        Assert.Equal(0, pool.ActiveLeases);
        var instance = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(instance);
        Assert.Equal(1, pool.ActiveLeases);

        pool.Release(instance);
        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Lease_Queues_Request()
    {
        var instance1 = await pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);

        var instance2 = await pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(2, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);

        var task = pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(2, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(1, pool.QueuedLeases);
        Assert.False(task.IsCompleted);

        pool.Release(instance1);
        Assert.Equal(2, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);
        Assert.True(task.IsCompleted);

        var instance3 = await task;
        Assert.NotNull(instance3);

        pool.Release(instance3);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(1, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);

        pool.Release(instance2);
        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(2, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);
    }

    [Fact]
    public async Task Lease_Returns_Ready_Item()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(instance.IsConnected);

        pool.Release(instance);
    }

    [Fact]
    public async Task Queued_Request_Timesout()
    {
        var instance1 = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var instance2 = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, pool.ActiveLeases);
        try
        {
            var exception = await Assert
                .ThrowsAsync<TaskCanceledException>(async () =>
                    await pool.LeaseAsync(CancellationToken.None));
            Assert.Contains("A task was canceled.", exception.Message);
        }
        finally
        {
            pool.Release(instance1);
            pool.Release(instance2);
        }
    }

    [Fact]
    public async Task NeverExceeds_MaxSize()
    {
        Assert.Equal(0, pool.ActiveLeases);

        var instance1 = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(instance1);
        Assert.Equal(1, pool.ActiveLeases);

        var instance2 = await pool.LeaseAsync(CancellationToken.None);
        Assert.NotNull(instance2);
        Assert.Equal(2, pool.ActiveLeases);

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(async () => await pool.LeaseAsync(CancellationToken.None));

        Assert.Contains("A task was canceled.", exception.Message);
        Assert.Equal(2, pool.ActiveLeases);
    }

    [Fact]
    public async Task Idle_Timeout_Removes_Items()
    {
        var options = new PoolOptions
        {
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
            IdleTimeout = TimeSpan.FromMilliseconds(0),
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, new EchoPreparationStrategy(), options);

        var instance = await pool.LeaseAsync(CancellationToken.None);
        pool.Release(instance);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        _ = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(instance.IsDisposed());
    }

    [Fact]
    public async Task Preparation_Strategy_Is_Applied()
    {
        var preparationStrategy = new EchoPreparationStrategy();
        var options = new PoolOptions
        {
            UseDefaultPreparationStrategy = false,
            UseDefaultFactory = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, preparationStrategy, options);

        var instance = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(await preparationStrategy.IsReadyAsync(instance, CancellationToken.None));

        pool.Release(instance);
    }

    [Fact]
    public async Task Preparation_Failure_Discards_Item_And_Serves_Fresh_One()
    {
        var preparationStrategy = new ThrowOnceEchoPreparationStrategy();
        var options = new PoolOptions
        {
            MinSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, preparationStrategy, options);
        Assert.Equal(1, pool.ItemsAllocated);

        // the preparation failure propagates so the caller can retry the lease
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        // the broken item was disposed and removed, not returned to the pool to poison the next leaser
        Assert.NotNull(preparationStrategy.FailedItem);
        Assert.True(preparationStrategy.FailedItem!.IsDisposed());
        Assert.Equal(0, pool.ItemsAllocated);
        Assert.Equal(0, pool.ItemsAvailable);

        // the freed slot lets the next lease create and prepare a fresh item
        var fresh = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(preparationStrategy.FailedItem, fresh);
        Assert.False(fresh.IsDisposed());
        Assert.True(fresh.IsConnected);

        pool.Release(fresh);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Concurrent_Leases_Are_Handled()
    {
        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            Logger,
            metrics,
            new EchoPreparationStrategy(), new PoolOptions
            {
                IdleTimeout = TimeSpan.FromMinutes(1),
                LeaseTimeout = TimeSpan.FromSeconds(10),
                MaxSize = 10,
                MinSize = 5,
                PreparationTimeout = TimeSpan.FromMinutes(1),
                UseDefaultFactory = false,
                UseDefaultPreparationStrategy = false,
            });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => pool.LeaseAsync().AsTask())
            .ToArray();

        var instances = await Task.WhenAll(tasks);

        Assert.Equal(10, pool.ActiveLeases);

        foreach (var instance in instances)
        {
            pool.Release(instance);
        }

        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Clear_Clears_Pool()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, pool.ItemsAllocated);

        pool.Release(instance);
        Assert.Equal(1, pool.ItemsAllocated);

        pool.Clear();

        Assert.Equal(1, pool.ItemsAllocated);
        Assert.Equal(1, pool.ItemsAvailable);
    }
}

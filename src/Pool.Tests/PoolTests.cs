using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

// todo: add metrics tests https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#test-custom-metrics

public sealed class PoolTests(IPool<IEcho> pool, IPoolMetrics metrics)
{
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

        await pool.ReleaseAsync(instance, CancellationToken.None);
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

        await pool.ReleaseAsync(instance1, CancellationToken.None);
        Assert.Equal(2, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);
        Assert.True(task.IsCompleted);

        var instance3 = await task;
        Assert.NotNull(instance3);

        await pool.ReleaseAsync(instance3, CancellationToken.None);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(1, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);

        await pool.ReleaseAsync(instance2, CancellationToken.None);
        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(2, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);
    }

    [Fact]
    public async Task Lease_Returns_Ready_Item()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(instance.IsConnected);

        await pool.ReleaseAsync(instance, CancellationToken.None);
    }

    [Fact]
    public async Task Queued_Request_Timesout()
    {
        var instance1 = await pool.LeaseAsync();
        var instance2 = await pool.LeaseAsync();
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
            await pool.ReleaseAsync(instance1, CancellationToken.None);
            await pool.ReleaseAsync(instance2, CancellationToken.None);
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

        using var pool = new Pool<IEcho>(metrics, new EchoFactory(), new EchoPreparationStrategy(), options);

        var instance = await pool.LeaseAsync(CancellationToken.None);
        await pool.ReleaseAsync(instance, CancellationToken.None);
        await Task.Delay(10);
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

        using var pool = new Pool<IEcho>(metrics, new EchoFactory(), preparationStrategy, options);

        var instance = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(await preparationStrategy.IsReadyAsync(instance, CancellationToken.None));

        await pool.ReleaseAsync(instance, CancellationToken.None);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Concurrent_Leases_Are_Handled()
    {
        using var pool = new Pool<IEcho>(
            metrics,
            new EchoFactory(),
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
            await pool.ReleaseAsync(instance, CancellationToken.None);
        }

        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task ClearAsync_Clears_Pool()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, pool.ItemsAllocated);

        await pool.ReleaseAsync(instance, CancellationToken.None);
        Assert.Equal(1, pool.ItemsAllocated);

        await pool.ClearAsync(CancellationToken.None);

        Assert.Equal(1, pool.ItemsAllocated);
        Assert.Equal(1, pool.ItemsAvailable);
    }
}

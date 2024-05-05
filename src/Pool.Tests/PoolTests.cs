using Pool.Tests.Fakes;

namespace Pool.Tests;

public sealed class PoolTests(IPool<IEcho> pool)
{
    [Fact]
    public void Pool_Is_Injected()
    {
        Assert.NotNull(pool);
    }

    [Fact]
    public void Allocated_Matches_Min()
    {
        Assert.Equal(1, pool.ItemsAllocated);
    }

    [Fact]
    public void Available_Matches_Allocated()
    {
        Assert.Equal(pool.ItemsAllocated, pool.ItemsAvailable);
    }

    [Fact]
    public void Backlog_Is_Empty()
    {
        Assert.Equal(0, pool.QueuedLeases);
    }

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

        var task = pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(1, pool.QueuedLeases);
        Assert.False(task.IsCompleted);

        await pool.ReleaseAsync(instance1, CancellationToken.None);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);
        Assert.True(task.IsCompleted);

        var instance2 = await task;
        Assert.NotNull(instance2);

        await pool.ReleaseAsync(instance2, CancellationToken.None);
        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Lease_Returns_Ready_Item()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(instance.IsReady);

        await pool.ReleaseAsync(instance, CancellationToken.None);
    }

    [Fact]
    public async Task Queued_Request_Timesout()
    {
        var instance1 = await pool.LeaseAsync(CancellationToken.None);
        Assert.Equal(1, pool.ActiveLeases);
        try
        {
            var execption = await Assert
                .ThrowsAsync<TaskCanceledException>(async () =>
                    await pool.LeaseAsync(CancellationToken.None));
        }
        finally
        {
            await pool.ReleaseAsync(instance1, CancellationToken.None);
        }
    }
}

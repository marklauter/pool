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
        Assert.Equal(1, pool.Allocated);
    }

    [Fact]
    public void Available_Matches_Allocated()
    {
        Assert.Equal(pool.Allocated, pool.Available);
    }

    [Fact]
    public async Task Lease_And_Release()
    {
        Assert.Equal(0, pool.ActiveLeases);
        var instance = await pool.LeaseAsync();
        Assert.NotNull(instance);
        Assert.Equal(1, pool.ActiveLeases);
        pool.Release(instance);
        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Lease_Queues_Request()
    {
        var instance1 = await pool.LeaseAsync();
        Assert.Equal(1, pool.ActiveLeases);

        var task = pool.LeaseAsync();
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(0, pool.Available);
        Assert.Equal(1, pool.Backlog);
        Assert.False(task.IsCompleted);

        pool.Release(instance1);
        Assert.Equal(1, pool.ActiveLeases);
        Assert.Equal(0, pool.Available);
        Assert.Equal(0, pool.Backlog);
        Assert.True(task.IsCompleted);

        var instance2 = await task;
        Assert.NotNull(instance2);

        pool.Release(instance2);
        Assert.Equal(0, pool.ActiveLeases);
    }
}

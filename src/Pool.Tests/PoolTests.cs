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

        var task = pool.LeaseAsync(CancellationToken.None);
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

    private static async Task<bool> IsReadyAsync(IEcho echo, CancellationToken cancellationToken)
    {
        return await Task.FromResult(echo.IsReady);
    }

    private static async Task MakeReadyAsync(IEcho echo, CancellationToken cancellationToken)
    {
        echo.MakeReady();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Lease_And_Make_Ready()
    {
        var instance = await pool.LeaseAsync(CancellationToken.None);
        Assert.False(instance.IsReady);

        pool.Release(instance);

        instance = await pool.LeaseAsync(
            TimeSpan.FromMinutes(1),
            IsReadyAsync,
            MakeReadyAsync,
            CancellationToken.None);

        Assert.True(instance.IsReady);

        pool.Release(instance);
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
                    await pool.LeaseAsync(
                        TimeSpan.FromMilliseconds(10),
                        CancellationToken.None));
        }
        finally
        {
            pool.Release(instance1);
        }
    }
}

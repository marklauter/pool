
using Pool.Tests.Fakes;

namespace Pool.Tests;
public sealed class PoolMapTests(IPoolMap<string, IEcho> pool)
{

    [Fact]
    public void Pool_Is_Injected() => Assert.NotNull(pool);

    [Fact]
    public async Task Lease_And_Release()
    {
        Assert.Equal(0, pool.UniqueLeases);
        var instance = await pool.LeaseAsync("testing", CancellationToken.None);
        Assert.NotNull(instance);
        Assert.Equal(1, pool.UniqueLeases);
        await pool.ReleaseAsync("testing", instance, CancellationToken.None);
    }

    [Fact]
    public async Task Lease_Unique_Request()
    {
        var instance1 = await pool.LeaseAsync("testing", CancellationToken.None);
        Assert.Equal(1, pool.UniqueLeases);

        var task = pool.LeaseAsync("testing", CancellationToken.None);
        Assert.Equal(1, pool.UniqueLeases);

        await pool.ReleaseAsync("testing", instance1, CancellationToken.None);
        Assert.Equal(1, pool.UniqueLeases);

        var instance2 = await task;
        Assert.NotNull(instance2);

        await pool.ReleaseAsync("testing", instance2, CancellationToken.None);
    }

    [Fact]
    public async Task Lease_Returns_Ready_Item()
    {
        var instance = await pool.LeaseAsync("testing", CancellationToken.None);

        Assert.True(instance.IsConnected);

        await pool.ReleaseAsync("testing", instance, CancellationToken.None);
    }

    [Fact]
    public async Task Queued_Request_Timesout()
    {
        var instance1 = await pool.LeaseAsync("testing", CancellationToken.None);
        Assert.Equal(1, pool.UniqueLeases);
        try
        {
            var execption = await Assert
                .ThrowsAsync<TaskCanceledException>(async () =>
                    await pool.LeaseAsync("testing", CancellationToken.None));
        }
        finally
        {
            await pool.ReleaseAsync("testing", instance1, CancellationToken.None);
        }
    }
}

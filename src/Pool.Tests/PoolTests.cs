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
        Assert.Equal(10, pool.Allocated);
    }

    [Fact]
    public void Available_Matches_Allocated()
    {
        Assert.Equal(pool.Allocated, pool.Available);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "need precise order for unit test")]
    public async Task Dispose_Releases_Item_Back_To_Pool()
    {
        Assert.Equal(0, pool.ActiveLeases);
        var instance = await pool.LeaseAsync();
        try
        {
            Assert.Equal(1, pool.ActiveLeases);
        }
        finally
        {
            instance.Dispose();
        }

        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Pool_Returns_Instance()
    {
        using var instance = await pool.LeaseAsync();
        Assert.NotNull(instance);
    }

    [Fact]
    public async Task Proxy_Works()
    {
        using var instance = await pool.LeaseAsync();
        var content = "hello world";
        Assert.Equal(content, instance.Shout(content));
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class UnboundedPoolTests
{
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    private static readonly ILogger<UnboundedPool<IEcho>> Logger = LoggerFactory.CreateLogger<UnboundedPool<IEcho>>();

    private static readonly IPoolMetrics Metrics = new NoopPoolMetrics();

    private static UnboundedPool<IEcho> CreatePool(
        UnboundedPoolOptions options,
        IItemFactory<IEcho>? factory = null,
        IPreparationStrategy<IEcho>? preparationStrategy = null,
        TimeProvider? timeProvider = null) =>
        new(factory ?? new EchoFactory(), Logger, Metrics, preparationStrategy, options, timeProvider);

    [Fact]
    public void Name_Is_TypeName_Dot_UnboundedPool()
    {
        using var pool = CreatePool(new UnboundedPoolOptions());
        Assert.Equal("IEcho.UnboundedPool", pool.Name);
    }

    [Fact]
    public void Backlog_Is_Always_Empty()
    {
        using var pool = CreatePool(new UnboundedPoolOptions());
        Assert.Equal(0, pool.QueuedLeases);
    }

    [Fact]
    public async Task Lease_Never_Waits_And_Rents_Without_Limit()
    {
        // MaxIdle bounds retention, not rent: leasing far past it must still succeed immediately and
        // hand out distinct items — the defining property of an unbounded pool
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 2 });
        var ct = TestContext.Current.CancellationToken;

        var items = new List<IEcho>();
        for (var i = 0; i < 50; i++)
        {
            items.Add(await pool.LeaseAsync(ct));
        }

        Assert.Equal(50, items.Count);
        Assert.Equal(50, items.Distinct().Count());
        Assert.Equal(50, pool.ActiveLeases);
        Assert.Equal(50, pool.ItemsAllocated);
        Assert.Equal(0, pool.ItemsAvailable);

        foreach (var item in items)
        {
            pool.Release(item);
        }
    }

    [Fact]
    public async Task Lease_Increments_And_Release_Decrements_Active()
    {
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, pool.ActiveLeases);
        var item = await pool.LeaseAsync(ct);
        Assert.Equal(1, pool.ActiveLeases);

        pool.Release(item);
        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task Release_Under_Cap_Retains_For_Reuse()
    {
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        var item = await pool.LeaseAsync(ct);
        pool.Release(item);
        Assert.Equal(1, pool.ItemsAvailable);

        // a retained item is reused rather than re-created
        var reused = await pool.LeaseAsync(ct);
        Assert.Same(item, reused);
        Assert.False(reused.IsDisposed());

        pool.Release(reused);
    }

    [Fact]
    public async Task Release_Over_Cap_Drops_And_Disposes_The_Overflow()
    {
        // MaxIdle 1: the first return is retained, the second overflows the cap and is dropped+disposed
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 1 });
        var ct = TestContext.Current.CancellationToken;

        var first = await pool.LeaseAsync(ct);
        var second = await pool.LeaseAsync(ct);

        pool.Release(first);
        Assert.Equal(1, pool.ItemsAvailable);
        Assert.False(first.IsDisposed());

        pool.Release(second);
        // cap held: the overflow item was disposed, not pooled
        Assert.Equal(1, pool.ItemsAvailable);
        Assert.True(second.IsDisposed());
        Assert.False(first.IsDisposed());
    }

    [Fact]
    public async Task MaxIdle_Zero_Drops_Every_Return()
    {
        // pure allocate-on-lease: nothing is ever retained, every returned item is disposed
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 0 });
        var ct = TestContext.Current.CancellationToken;

        var item = await pool.LeaseAsync(ct);
        pool.Release(item);

        Assert.Equal(0, pool.ItemsAvailable);
        Assert.True(item.IsDisposed());
    }

    [Fact]
    public async Task Idle_Item_Past_Timeout_Is_Disposed_And_Fresh_One_Served()
    {
        // a controllable clock makes idle expiry deterministic: no Task.Delay, no wall-clock race
        var timeProvider = new FakeTimeProvider();
        using var pool = CreatePool(
            new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4, IdleTimeout = TimeSpan.FromMinutes(5) },
            timeProvider: timeProvider);
        var ct = TestContext.Current.CancellationToken;

        var instance = await pool.LeaseAsync(ct);
        pool.Release(instance);

        // push the clock past the idle timeout; the next lease evicts (disposes) the stale item and serves fresh
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var fresh = await pool.LeaseAsync(ct);

        Assert.True(instance.IsDisposed());
        Assert.NotSame(instance, fresh);

        pool.Release(fresh);
    }

    [Fact]
    public async Task Unreleased_Lease_Stays_Outstanding_And_Is_Owned_By_The_Caller()
    {
        // ownership-transfer semantics: a leased item the caller never returns stays counted as
        // outstanding, and the pool does not dispose it on its own disposal — the caller owns it
        var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        var item = await pool.LeaseAsync(ct);
        Assert.Equal(1, pool.ActiveLeases);

        // leasing more is unaffected by the un-returned item (no capacity to starve)
        var another = await pool.LeaseAsync(ct);
        Assert.Equal(2, pool.ActiveLeases);
        pool.Release(another);

        pool.Dispose();

        // the pool disposes only idle items; the outstanding leased item belongs to the caller
        Assert.False(item.IsDisposed());
        item.Dispose();
    }

    [Fact]
    public async Task Preparation_Failure_Discards_Item_And_Does_Not_Count_The_Lease()
    {
        var preparationStrategy = new ThrowOnceEchoPreparationStrategy();
        using var pool = CreatePool(
            new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 },
            preparationStrategy: preparationStrategy);
        var ct = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.LeaseAsync(ct));

        // the broken item was disposed, the lease was never counted, and nothing was pooled
        Assert.NotNull(preparationStrategy.FailedItem);
        Assert.True(preparationStrategy.FailedItem!.IsDisposed());
        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAllocated);

        // the caller can retry and get a fresh, prepared item
        var fresh = await pool.LeaseAsync(ct);
        Assert.NotSame(preparationStrategy.FailedItem, fresh);
        Assert.True(fresh.IsConnected);
        pool.Release(fresh);
    }

    [Fact]
    public async Task Preparation_Runs_Once_And_Is_Skipped_For_Reused_Item()
    {
        var preparationStrategy = new CountingEchoPreparationStrategy();
        using var pool = CreatePool(
            new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 },
            preparationStrategy: preparationStrategy);
        var ct = TestContext.Current.CancellationToken;

        var instance = await pool.LeaseAsync(ct);
        Assert.Equal(1, preparationStrategy.PrepareCount);
        Assert.True(instance.IsConnected);

        pool.Release(instance);
        var reused = await pool.LeaseAsync(ct);
        Assert.Same(instance, reused);
        Assert.Equal(1, preparationStrategy.PrepareCount);

        pool.Release(reused);
    }

    [Fact]
    public void Seed_Respects_MaxIdle()
    {
        // MinSize beyond MaxIdle is clamped so the pool never starts over its own retention cap
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 5, MaxIdle = 2 });
        Assert.Equal(2, pool.ItemsAvailable);
        Assert.Equal(2, pool.ItemsAllocated);
    }

    [Fact]
    public async Task Clear_Disposes_Idle_Items_And_Refills_With_Fresh_Ones()
    {
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 2, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        var first = await pool.LeaseAsync(ct);
        var second = await pool.LeaseAsync(ct);
        pool.Release(first);
        pool.Release(second);
        Assert.Equal(2, pool.ItemsAvailable);

        pool.Clear();

        Assert.True(first.IsDisposed());
        Assert.True(second.IsDisposed());
        Assert.Equal(2, pool.ItemsAvailable);
        Assert.Equal(2, pool.ItemsAllocated);

        var fresh = await pool.LeaseAsync(ct);
        Assert.NotSame(first, fresh);
        Assert.NotSame(second, fresh);
        Assert.False(fresh.IsDisposed());
        pool.Release(fresh);
    }

    [Fact]
    public async Task Dispose_Disposes_Idle_Items_Only()
    {
        var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        // hold two distinct items at once, then return one so a known instance sits idle while the
        // other stays leased — otherwise releasing and re-leasing would just reuse the same instance
        var idle = await pool.LeaseAsync(ct);
        var leased = await pool.LeaseAsync(ct);
        pool.Release(idle); // becomes idle, owned by the pool; leased stays out, owned by the caller

        pool.Dispose();

        Assert.True(idle.IsDisposed());
        Assert.False(leased.IsDisposed());
        leased.Dispose();
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test deliberately exercises post-dispose rejection")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test deliberately calls Dispose to verify idempotency and post-dispose rejection")]
    public async Task Disposed_Pool_Rejects_Operations_And_Tolerates_Double_Dispose()
    {
        var pool = CreatePool(new UnboundedPoolOptions { MinSize = 1, MaxIdle = 4 });
        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        pool.Dispose();
        pool.Dispose(); // idempotent

        _ = Assert.Throws<ObjectDisposedException>(() => pool.Release(item));
        _ = Assert.Throws<ObjectDisposedException>(pool.Clear);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        item.Dispose();
    }

    [Fact]
    public void Release_Null_Throws()
    {
        using var pool = CreatePool(new UnboundedPoolOptions());
        _ = Assert.Throws<ArgumentNullException>(() => pool.Release(null!));
    }

    [Fact]
    public async Task Ctor_Without_Preparation_Strategy_Skips_Preparation()
    {
        // exercises the prep-less constructor overload: items are handed out without a readiness step
        using var pool = new UnboundedPool<IEcho>(new EchoFactory(), Logger, Metrics, new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });

        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(item);
        Assert.False(item.IsConnected); // no preparation strategy ran
        pool.Release(item);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "each constructor guard clause throws before construction completes, so no instance exists to dispose")]
    public void Ctor_Null_Arguments_Throw()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => new UnboundedPool<IEcho>(null!, Logger, Metrics, new UnboundedPoolOptions()));
        _ = Assert.Throws<ArgumentNullException>(
            () => new UnboundedPool<IEcho>(new EchoFactory(), null!, Metrics, new UnboundedPoolOptions()));
        _ = Assert.Throws<ArgumentNullException>(
            () => new UnboundedPool<IEcho>(new EchoFactory(), Logger, null!, new UnboundedPoolOptions()));
        _ = Assert.Throws<ArgumentNullException>(
            () => new UnboundedPool<IEcho>(new EchoFactory(), Logger, Metrics, (UnboundedPoolOptions)null!));
    }

    [Fact]
    public void Ctor_Negative_MinSize_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreatePool(new UnboundedPoolOptions { MinSize = -1 }));

    [Fact]
    public void Ctor_Negative_MaxIdle_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreatePool(new UnboundedPoolOptions { MaxIdle = -1 }));

    [Fact]
    public void Ctor_Factory_Failure_Mid_Seed_Disposes_Already_Created_Items()
    {
        // the seeded items created before the failure must be disposed, leaving no leak behind
        var factory = new ThrowAfterCountItemFactory(throwAfter: 2);

        _ = Assert.Throws<InvalidOperationException>(
            () => CreatePool(new UnboundedPoolOptions { MinSize = 5, MaxIdle = 10 }, factory));

        Assert.Equal(2, factory.CreatedItems.Count);
        Assert.All(factory.CreatedItems, echo => Assert.True(echo.IsDisposed()));
    }

    [Fact]
    public async Task Already_Cancelled_Token_Cancels_The_Lease()
    {
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pool.LeaseAsync(cts.Token));
        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    public async Task LeaseScope_Returns_Item_On_Dispose()
    {
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = 4 });
        var ct = TestContext.Current.CancellationToken;

        IEcho leased;
        using (var lease = await pool.LeaseScopeAsync(ct))
        {
            leased = lease.Item;
            Assert.Equal(1, pool.ActiveLeases);
        }

        // disposing the scope returned the item to the pool for reuse
        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(1, pool.ItemsAvailable);
        Assert.False(leased.IsDisposed());
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Concurrent_Churn_Never_Aliases_And_Respects_Idle_Cap()
    {
        const int maxIdle = 8;
        using var pool = CreatePool(new UnboundedPoolOptions { MinSize = 0, MaxIdle = maxIdle });

        var held = new ConcurrentDictionary<IEcho, byte>();
        var doubleHandouts = 0;
        var capViolations = 0;
        var ct = TestContext.Current.CancellationToken;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 5_000),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (i, token) =>
            {
                var item = await pool.LeaseAsync(token);
                if (!held.TryAdd(item, 0))
                {
                    _ = Interlocked.Increment(ref doubleHandouts);
                }

                if (pool.ItemsAvailable > maxIdle)
                {
                    _ = Interlocked.Increment(ref capViolations);
                }

                Thread.SpinWait(200);
                _ = held.TryRemove(item, out _);
                pool.Release(item);
            });

        Assert.Equal(0, doubleHandouts);
        Assert.Equal(0, capViolations);
        Assert.Equal(0, pool.ActiveLeases);
        Assert.True(pool.ItemsAvailable <= maxIdle);
    }
}

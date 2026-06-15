using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pool.Tests;

public sealed class LeaseTests
{
    private static readonly string PoolName = Pool<IEcho>.PoolName;

    // the pool only needs the observer-registration handles back, which NSubstitute auto-provides;
    // these tests exercise the lease, not the metrics wiring covered elsewhere
    private static Pool<IEcho> CreatePool(int maxSize = 1) =>
        new(
            new EchoFactory(),
            NullLogger<Pool<IEcho>>.Instance,
            new NoopPoolMetrics(),
            new PoolOptions { MinSize = 0, MaxSize = maxSize, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });

    [Fact]
    public async Task LeaseScopeAsync_Leases_An_Item()
    {
        using var pool = CreatePool();
        using var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(lease.Item);
        Assert.Equal(1, pool.ActiveLeases);
    }

    [Fact]
    public async Task LeaseScopeAsync_Throws_On_Null_Pool()
    {
        IPool<IEcho>? pool = null;
        _ = await Assert.ThrowsAsync<ArgumentNullException>(
            () => pool!.LeaseScopeAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Disposing_Lease_Returns_Item()
    {
        using var pool = CreatePool();

        using (var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(1, pool.ActiveLeases);
        }

        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(1, pool.ItemsAvailable);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test deliberately accesses the disposed lease to verify use-after-return is blocked")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test disposes explicitly to assert use-after-return is blocked")]
    public async Task Item_After_Dispose_Throws()
    {
        using var pool = CreatePool();
        var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken);
        lease.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => lease.Item);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test deliberately double-disposes to verify the release is idempotent")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test disposes explicitly to control the double-dispose sequence")]
    public async Task Double_Dispose_Releases_Once()
    {
        using var pool = CreatePool(maxSize: 1);
        var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken);

        lease.Dispose();
        // a second dispose must be a no-op: no SemaphoreFullException, no duplicate enqueue
        lease.Dispose();

        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(1, pool.ItemsAvailable);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test disposes the pool before the lease to exercise the pool-already-disposed path")]
    public async Task Dispose_After_Pool_Disposed_Disposes_The_Item()
    {
        var pool = CreatePool();
        var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken);
        var echo = lease.Item;

        // pool gone while the item is out on lease
        pool.Dispose();

        // the lease can't return it, so it disposes the orphaned item instead — without throwing
        lease.Dispose();

        Assert.True(echo.IsDisposed());
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test inspects the disposed pool's idle count to confirm the orphaned item was not re-pooled")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test disposes the pool before the lease to exercise the pool-already-disposed path")]
    public async Task Dispose_After_Pool_Disposed_Tolerates_NonDisposable_Item()
    {
        var pool = new Pool<Widget>(
            new WidgetFactory(),
            NullLogger<Pool<Widget>>.Instance,
            new NoopPoolMetrics(),
            new PoolOptions { MinSize = 0, MaxSize = 1, UseDefaultFactory = false, UseDefaultPreparationStrategy = false });
        var lease = await pool.LeaseScopeAsync(TestContext.Current.CancellationToken);

        // pool gone and the item is not IDisposable, so dispose simply swallows — no throw
        pool.Dispose();
        lease.Dispose();

        Assert.Equal(0, pool.ItemsAvailable);
    }

    [Fact]
    public async Task Leaked_Lease_Is_Counted()
    {
        using var pool = CreatePool();
        using var collector = new MetricCollector<long>(LeaseLeakMetric.Leaked);

        await LeakALeaseAsync(pool, TestContext.Current.CancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var leaks = collector.GetMeasurementSnapshot()
            .Where(m => Equals(m.Tags["pool.name"], PoolName))
            .ToList();

        Assert.NotEmpty(leaks);
        Assert.All(leaks, m => Assert.Equal(1L, m.Value));
    }

    [Fact]
    public async Task Dispose_With_Failing_Release_Is_Not_Counted_As_Leak()
    {
        using var pool = CreatePool(maxSize: 1);
        using var collector = new MetricCollector<long>(LeaseLeakMetric.Leaked);

        await CorruptGateThenDisposeAsync(pool, TestContext.Current.CancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // the lease was disposed — Release merely threw — so the finalizer must have been suppressed
        // and no leak recorded
        Assert.DoesNotContain(collector.GetMeasurementSnapshot(), m => Equals(m.Tags["pool.name"], PoolName));
    }

    // leak a lease in a non-inlined frame so it has no rooted reference once this returns, letting
    // the GC finalize it and the finalizer record the leak
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP004:Don't ignore created IDisposable", Justification = "the test deliberately leaks the lease (never disposes it) to exercise the finalizer leak counter")]
    private static async Task LeakALeaseAsync(IPool<IEcho> pool, CancellationToken cancellationToken) =>
        _ = await pool.LeaseScopeAsync(cancellationToken);

    // force a non-ObjectDisposedException out of Release — a SemaphoreFullException from returning the
    // same item twice (once via the raw API, once via the lease) — then dispose the lease. the lease
    // was disposed, so it must not be counted as a leak even though Release threw. runs in a non-inlined
    // frame so the lease can be finalized once it returns (proving the finalizer was suppressed).
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "the lease is disposed via Assert.Throws to capture the expected Release failure")]
    private static async Task CorruptGateThenDisposeAsync(Pool<IEcho> pool, CancellationToken cancellationToken)
    {
        var lease = await pool.LeaseScopeAsync(cancellationToken);
        pool.Release(lease.Item);
        _ = Assert.Throws<SemaphoreFullException>(lease.Dispose);
    }

    // a pooled item that is not IDisposable, to cover the lease's "pool gone, nothing to dispose" path
    private sealed class Widget;

    private sealed class WidgetFactory : IItemFactory<Widget>
    {
        public Widget CreateItem() => new();
    }
}

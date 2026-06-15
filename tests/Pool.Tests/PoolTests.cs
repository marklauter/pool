using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Pool.Metrics;
using Pool.Tests.Fakes;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Pool.Tests;

public sealed class PoolTests(IPool<IEcho> pool, IPoolMetrics metrics, PoolOptions configuredOptions)
{
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    private static readonly ILogger<Pool<IEcho>> Logger = LoggerFactory.CreateLogger<Pool<IEcho>>();

    [Fact]
    public void Pool_Is_Injected() => Assert.NotNull(pool);

    [Fact]
    public void Allocated_Matches_Min() => Assert.Equal(configuredOptions.MinSize, pool.ItemsAllocated);

    [Fact]
    public void Available_Matches_Allocated() => Assert.Equal(pool.ItemsAllocated, pool.ItemsAvailable);

    [Fact]
    public void Backlog_Is_Empty() => Assert.Equal(0, pool.QueuedLeases);

    [Fact]
    public void Name_Is_TypeName_Dot_Pool() => Assert.Equal("IEcho.Pool", pool.Name);

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

        // releasing the permit wakes the queued waiter, but SemaphoreSlim resumes it on the thread
        // pool, so the item dequeue and the QueuedLeases decrement land only once the hand-off is awaited
        var instance3 = await task;
        Assert.NotNull(instance3);
        Assert.Equal(2, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
        Assert.Equal(0, pool.QueuedLeases);

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
    public async Task Queued_Request_Times_Out()
    {
        // saturate capacity so the next lease must queue, then let it time out (Startup LeaseTimeout = 10ms)
        var instance1 = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var instance2 = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(configuredOptions.MaxSize, pool.ActiveLeases);
        try
        {
            _ = await Assert.ThrowsAsync<TaskCanceledException>(
                async () => await pool.LeaseAsync(CancellationToken.None));

            // the timed-out request is purged from the backlog, not left dangling
            Assert.Equal(0, pool.QueuedLeases);
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
        Assert.Equal(configuredOptions.MaxSize, pool.ActiveLeases);

        // a lease beyond capacity times out rather than allocating a third item
        _ = await Assert.ThrowsAsync<TaskCanceledException>(
            async () => await pool.LeaseAsync(CancellationToken.None));

        // the cap held: nothing was created past MaxSize
        Assert.Equal(configuredOptions.MaxSize, pool.ItemsAllocated);
        Assert.Equal(configuredOptions.MaxSize, pool.ActiveLeases);
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
        // IdleTimeout = 0 means the released item is already expired, so the next dequeue evicts it
        // without any elapsed-time dependency — no Task.Delay needed
        _ = await pool.LeaseAsync(CancellationToken.None);

        Assert.True(instance.IsDisposed());
    }

    [Fact]
    public async Task Idle_Item_Past_Timeout_Is_Disposed_And_Fresh_One_Served()
    {
        // a controllable clock makes idle expiry deterministic: no Task.Delay, no wall-clock race (M4)
        var timeProvider = new FakeTimeProvider();
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
            IdleTimeout = TimeSpan.FromMinutes(5),
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options, timeProvider);

        // lease then return so a single known instance sits idle, stamped at the fake clock's current time
        var instance = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(instance);

        // push the clock past the idle timeout; the next lease must evict (dispose) the stale item and serve fresh
        timeProvider.Advance(TimeSpan.FromMinutes(6));
        var fresh = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        Assert.True(instance.IsDisposed());
        Assert.NotSame(instance, fresh);
    }

    [Fact]
    public async Task Lease_Times_Out_Deterministically_When_Capacity_Stays_Saturated()
    {
        // the lease timeout is now CTS-driven off timeProvider, so a FakeTimeProvider trips it with no real wait (M4)
        var timeProvider = new FakeTimeProvider();
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
            LeaseTimeout = TimeSpan.FromSeconds(30),
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options, timeProvider);

        // hold the only permit so the next lease must queue and wait for the timeout
        _ = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        // LeaseAsync runs synchronously up to its WaitAsync, so the waiter is already parked with its timeout CTS created
        var pending = pool.LeaseAsync(CancellationToken.None).AsTask();
        Assert.Equal(1, pool.QueuedLeases);

        timeProvider.Advance(TimeSpan.FromSeconds(31));

        _ = await Assert.ThrowsAsync<TaskCanceledException>(() => pending);
    }

    [Fact]
    public async Task Preparation_Times_Out_Deterministically_When_Strategy_Stalls()
    {
        // the preparation timeout is CTS-driven off timeProvider; a stalled strategy + FakeTimeProvider trips it (M4)
        var timeProvider = new FakeTimeProvider();
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
            PreparationTimeout = TimeSpan.FromSeconds(30),
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, new BlockingEchoPreparationStrategy(), options, timeProvider);

        // the lease acquires a permit and parks in PrepareAsync with its preparation-timeout CTS created
        var pending = pool.LeaseAsync(CancellationToken.None).AsTask();

        timeProvider.Advance(TimeSpan.FromSeconds(31));

        _ = await Assert.ThrowsAsync<TaskCanceledException>(() => pending);

        // the failed preparation disposed the item and released the permit, restoring capacity
        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAvailable);
    }

    [Fact]
    public async Task Preparation_Strategy_Is_Applied()
    {
        // a counting spy proves the pool actually ran preparation, rather than re-deriving readiness
        var preparationStrategy = new CountingEchoPreparationStrategy();
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultPreparationStrategy = false,
            UseDefaultFactory = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, preparationStrategy, options);

        // first lease creates an unprepared item, so the pool runs PrepareAsync exactly once
        var instance = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, preparationStrategy.PrepareCount);
        Assert.True(instance.IsConnected);

        // returning and re-leasing the now-ready item must not prepare it again
        pool.Release(instance);
        var reused = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Same(instance, reused);
        Assert.Equal(1, preparationStrategy.PrepareCount);

        pool.Release(reused);
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
        Assert.Equal(10, instances.Distinct().Count()); // never hand the same item to two leasers

        foreach (var instance in instances)
        {
            pool.Release(instance);
        }

        Assert.Equal(0, pool.ActiveLeases);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Lease_Does_Not_Lose_Wakeups_Under_Concurrency()
    {
        // Regression test for the lost-wakeup race (findings I1).
        //
        // A lost wakeup only hangs under quiescence: a waiter is enqueued just after a release
        // dropped its item into the idle pool without seeing the waiter, and no further release
        // follows to heal it. Continuous churn hides the bug (the next release serves the stuck
        // waiter), so this provokes the race in isolation: hold the single slot, then release it
        // and start a waiter *simultaneously* (a Barrier aligns the two threads) so they collide
        // in LeaseAsync's check-then-enqueue window. With nothing else to heal a lost wakeup, the
        // waiter blocks until LeaseTimeout and throws TaskCanceledException -> the test goes red on
        // the racy implementation and green on the SemaphoreSlim rendezvous.
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            LeaseTimeout = TimeSpan.FromSeconds(2),
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(
            new EchoFactory(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Pool<IEcho>>.Instance,
            metrics,
            new EchoPreparationStrategy(),
            options);

        var ct = TestContext.Current.CancellationToken;
        const int iterations = 2000;
        for (var i = 0; i < iterations; i++)
        {
            // occupy the only slot so the waiter below must queue
            var held = await pool.LeaseAsync(ct);

            // align two threads so Release and the queuing lease collide in the check-then-enqueue window
            var ready = new int[1];
            void Align()
            {
                _ = Interlocked.Increment(ref ready[0]);
                SpinWait.SpinUntil(() => Volatile.Read(ref ready[0]) >= 2);
            }

            var releaseTask = Task.Run(() =>
            {
                Align();
                pool.Release(held);
            }, ct);
            var waiterTask = Task.Run(() =>
            {
                Align();
                return pool.LeaseAsync(ct).AsTask();
            }, ct);

            // if the wakeup is lost the waiter never completes and this throws TaskCanceledException
            var served = await waiterTask;
            await releaseTask;

            Assert.NotNull(served);
            Assert.False(served.IsDisposed());
            pool.Release(served);
        }
    }

    [Fact]
    public async Task Clear_Disposes_Idle_Items_And_Refills_With_Fresh_Ones()
    {
        var options = new PoolOptions
        {
            MinSize = 2,
            MaxSize = 4,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options);

        // lease the seeded items out and return them, so the pool holds known instances
        var first = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var second = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        pool.Release(first);
        pool.Release(second);
        Assert.Equal(2, pool.ItemsAvailable);

        pool.Clear();

        // the old idle items are disposed, not recirculated
        Assert.True(first.IsDisposed());
        Assert.True(second.IsDisposed());

        // the pool is refilled to MinSize with fresh, undisposed instances
        Assert.Equal(2, pool.ItemsAllocated);
        Assert.Equal(2, pool.ItemsAvailable);

        var fresh = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(first, fresh);
        Assert.NotSame(second, fresh);
        Assert.False(fresh.IsDisposed());

        pool.Release(fresh);
    }

    [Fact]
    public async Task Clear_Does_Not_Over_Allocate_For_Queued_Requests()
    {
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options);

        // saturate the single slot, then queue a request that can't be filled immediately
        var held = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        var queued = pool.LeaseAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.Equal(1, pool.QueuedLeases);

        // strict-max: Clear cannot manufacture an item past MaxSize to hand to the waiter, so the
        // waiter stays queued and the pool does not over-allocate
        pool.Clear();
        Assert.False(queued.IsCompleted);
        Assert.Equal(1, pool.QueuedLeases);
        Assert.Equal(1, pool.ActiveLeases);

        // releasing the held lease is what serves the waiter
        pool.Release(held);
        var served = await queued;
        Assert.NotNull(served);
        Assert.Equal(0, pool.QueuedLeases);

        pool.Release(served);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP016:Don't use disposed instance", Justification = "the test deliberately exercises post-dispose rejection")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP017:Prefer using", Justification = "the test deliberately calls Dispose to verify idempotency and post-dispose rejection")]
    public async Task Disposed_Pool_Rejects_Operations_And_Tolerates_Double_Dispose()
    {
        var options = new PoolOptions
        {
            MinSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options);
        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        pool.Dispose();
        pool.Dispose(); // idempotent: a second dispose is a no-op, not a throw

        _ = Assert.Throws<ObjectDisposedException>(() => pool.Release(item));
        _ = Assert.Throws<ObjectDisposedException>(pool.Clear);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Utilization_Rate_Observer_Reports_Active_Over_Allocated()
    {
        // capture the observer the pool registers and invoke it directly, exercising both arms of
        // the utilization lambda (ItemsAllocated == 0 ? 0 : ActiveLeases / ItemsAllocated)
        var capturingMetrics = new CapturingPoolMetrics();
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 2,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, capturingMetrics, options);
        Assert.NotNull(capturingMetrics.UtilizationRate);

        // empty pool: ItemsAllocated == 0 -> utilization is 0
        Assert.Equal(0d, capturingMetrics.UtilizationRate!());

        // one item leased of one allocated -> ActiveLeases / ItemsAllocated == 1.0
        var item = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1d, capturingMetrics.UtilizationRate!());

        pool.Release(item);
    }

    [Fact]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP013:Await in using", Justification = "it's not")]
    public async Task Concurrent_Churn_Never_Hands_One_Item_To_Two_Callers()
    {
        // the core pool invariant under contention: with fewer permits than workers, no instance is
        // ever held by two callers at once. each worker marks its leased item in a shared set (a
        // collision means the pool aliased) and briefly holds it (a CPU spin, not a wall-clock wait,
        // so two aliased holders would overlap). deterministic: a correct pool yields zero violations.
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 4,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options);

        var held = new ConcurrentDictionary<IEcho, byte>();
        var doubleHandouts = 0;
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

                Thread.SpinWait(200); // widen the critical section so an aliasing regression overlaps and is caught
                _ = held.TryRemove(item, out _);
                pool.Release(item);
            });

        Assert.Equal(0, doubleHandouts);
        Assert.Equal(0, pool.ActiveLeases);
        Assert.True(pool.ItemsAllocated <= options.MaxSize);
    }

    [Fact]
    public async Task Caller_Cancellation_Cancels_A_Queued_Lease()
    {
        // LeaseTimeout is infinite, so the only thing that can end the queued lease is the caller's
        // token — this isolates caller-cancellation from the timeout path (no real wait, no Task.Delay)
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            LeaseTimeout = Timeout.InfiniteTimeSpan,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new EchoFactory(), Logger, metrics, options);

        // hold the only permit so the next lease must queue
        var held = await pool.LeaseAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var pending = pool.LeaseAsync(cts.Token).AsTask();
        Assert.Equal(1, pool.QueuedLeases);

        await cts.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // the cancelled waiter is purged and never consumed a permit
        Assert.Equal(0, pool.QueuedLeases);
        Assert.Equal(1, pool.ActiveLeases);

        // capacity is intact: releasing the held lease lets a fresh lease succeed
        pool.Release(held);
        var next = await pool.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        pool.Release(next);
    }

    [Fact]
    public void Release_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => pool.Release(null!));

    [Fact]
    public async Task Lease_Releases_Permit_When_Factory_Throws()
    {
        // MinSize 0 seeds nothing at construction, so the factory throw happens on the lease path
        // (TryAcquireItem); the permit taken for that lease must be released so capacity is restored
        var options = new PoolOptions
        {
            MinSize = 0,
            MaxSize = 1,
            UseDefaultFactory = false,
            UseDefaultPreparationStrategy = false,
        };

        using var pool = new Pool<IEcho>(new ThrowAfterCountItemFactory(throwAfter: 0), Logger, metrics, options);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await pool.LeaseAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0, pool.ActiveLeases);
        Assert.Equal(0, pool.ItemsAllocated);
    }

    // captures the utilization observer so a test can invoke the registered lambda directly,
    // without standing up a Meter / MetricCollector
    private sealed class CapturingPoolMetrics : IPoolMetrics
    {
        public Func<double>? UtilizationRate { get; private set; }

        public IDisposable RegisterUtilizationRateObserver(Func<double> observeValue)
        {
            UtilizationRate = observeValue;
            return NoopDisposable.Instance;
        }

        public IDisposable RegisterItemsAllocatedObserver(Func<int> observeValue) => NoopDisposable.Instance;
        public IDisposable RegisterItemsAvailableObserver(Func<int> observeValue) => NoopDisposable.Instance;
        public IDisposable RegisterActiveLeasesObserver(Func<int> observeValue) => NoopDisposable.Instance;
        public IDisposable RegisterQueuedLeasesObserver(Func<int> observeValue) => NoopDisposable.Instance;
        public void RecordLeaseException(Exception ex) { }
        public void RecordPreparationException(Exception ex) { }
        public void RecordLeaseWaitTime(TimeSpan duration) { }
        public void RecordPreparationTime(TimeSpan duration) { }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
